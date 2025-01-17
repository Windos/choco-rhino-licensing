﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.ServiceModel;
using System.Threading;
using System.Xml;
using Rhino.Licensing.Discovery;
using Rhino.Licensing.Logging;

namespace Rhino.Licensing
{
    /// <summary>
    /// Base license validator.
    /// </summary>
    public abstract class AbstractLicenseValidator
    {
        /// <summary>
        /// License validator logger
        /// </summary>
        private static readonly ILog Log = LogProvider.GetLogger(typeof(AbstractLicenseValidator));

        /// <summary>
        /// Standard Time servers
        /// </summary>
        protected static readonly string[] TimeServers =
        {
            "time.nist.gov",
            "time-nw.nist.gov",
            "time-a.nist.gov",
            "time-b.nist.gov",
            "time-a.timefreq.bldrdoc.gov",
            "time-b.timefreq.bldrdoc.gov",
            "time-c.timefreq.bldrdoc.gov",
            "utcnist.colorado.edu",
            "nist1.datum.com",
            "nist1.dc.certifiedtime.com",
            "nist1.nyc.certifiedtime.com",
            "nist1.sjc.certifiedtime.com"
        };

        /// <summary>
        /// The license server URL for floating licenses
        /// </summary>
        private readonly string licenseServerUrl;
        private readonly Guid clientId;
        private readonly string publicKey;
        private readonly Timer nextLeaseTimer;
        private bool disableFutureChecks;
        private bool currentlyValidatingSubscriptionLicense;
        private readonly DiscoveryHost discoveryHost;
        private DiscoveryClient discoveryClient;
        private Guid senderId;
        private int subscriptionCheckDays = 10;

        /// <summary>
        /// Fired when license data is invalidated
        /// </summary>
        public event Action<InvalidationType> LicenseInvalidated;

        /// <summary>
        /// Fired when license is expired
        /// </summary>
        public event Action<DateTime> LicenseExpired;

        /// <summary>
        /// Event that's raised when duplicate licenses are found
        /// </summary>
        public event EventHandler<DiscoveryHost.ClientDiscoveredEventArgs> MultipleLicensesWereDiscovered;

        /// <summary>
        /// Disable the <see cref="ExpirationDate"/> validation with the time servers
        /// </summary>
        public bool DisableTimeServersCheck
        {
            get; set;
        }

        /// <summary>
        /// Gets the expiration date of the license
        /// </summary>
        public DateTime ExpirationDate { get; private set; }

        /// <summary>
        /// Lease timeout
        /// </summary>
        public TimeSpan LeaseTimeout { get; set; }

        /// <summary>
        /// How to behave when using the same license multiple times
        /// </summary>
        public MultipleLicenseUsage MultipleLicenseUsageBehavior { get; set; }

        /// <summary>
        /// Gets the Type of the license
        /// </summary>
        public LicenseType LicenseType { get; private set; }

        /// <summary>
        /// Gets the Id of the license holder
        /// </summary>
        public Guid UserId { get; private set; }

        /// <summary>
        /// Gets the name of the license holder
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Gets extra license information
        /// </summary>
        public IDictionary<string, string> LicenseAttributes { get; private set; }

        /// <summary>
        /// Whether the client discovery server is enabled. This detects duplicate licenses used on the same network.
        /// </summary>
        public bool DiscoveryEnabled { get; private set; }

        /// <summary>
        /// Gets or Sets the license content
        /// </summary>
        protected abstract string License { get; set; }

        /// <summary>
        /// Creates a license validator with specfied public key.
        /// </summary>
        /// <param name="publicKey">public key</param>
        /// <param name="enableDiscovery">Whether to enable the client discovery server to detect duplicate licenses used on the same network.</param>
        protected AbstractLicenseValidator(string publicKey, bool enableDiscovery = true)
        {
            LeaseTimeout = TimeSpan.FromMinutes(5);
            LicenseAttributes = new Dictionary<string, string>();
            nextLeaseTimer = new Timer(LeaseLicenseAgain);
            this.publicKey = publicKey;

            DiscoveryEnabled = enableDiscovery;

            if (DiscoveryEnabled)
            {
                senderId = Guid.NewGuid();
                discoveryHost = new DiscoveryHost();
                discoveryHost.ClientDiscovered += DiscoveryHostOnClientDiscovered;
                discoveryHost.Start();
            }
        }

        /// <summary>
        /// Creates a license validator using the client information
        /// and a service endpoint address to validate the license.
        /// </summary>
        protected AbstractLicenseValidator(string publicKey, string licenseServerUrl, Guid clientId)
            : this(publicKey)
        {
            this.licenseServerUrl = licenseServerUrl;
            this.clientId = clientId;
        }

        private void LeaseLicenseAgain(object state)
        {
            var client = discoveryClient;
            if (client != null)
                client.PublishMyPresence();

            if (HasExistingLicense())
                return;

            RaiseLicenseInvalidated();
        }

        private void RaiseLicenseInvalidated()
        {
            var licenseInvalidated = LicenseInvalidated;
            if (licenseInvalidated == null)
                throw new InvalidOperationException("License was invalidated, but there is no one subscribe to the LicenseInvalidated event");

            licenseInvalidated(LicenseType == LicenseType.Floating ? InvalidationType.CannotGetNewLicense :
                                                                     InvalidationType.TimeExpired);
        }

        private void RaiseMultipleLicenseDiscovered(DiscoveryHost.ClientDiscoveredEventArgs args)
        {
            var onMultipleLicensesWereDiscovered = MultipleLicensesWereDiscovered;
            if (onMultipleLicensesWereDiscovered != null)
            {
                onMultipleLicensesWereDiscovered(this, args);
            }
        }

        private void DiscoveryHostOnClientDiscovered(object sender, DiscoveryHost.ClientDiscoveredEventArgs clientDiscoveredEventArgs)
        {
            if (senderId == clientDiscoveredEventArgs.SenderId) // we got our own notification, ignore it
                return;

            if (UserId != clientDiscoveredEventArgs.UserId) // another license, we don't care
                return;

            // same user id, different senders
            switch (MultipleLicenseUsageBehavior)
            {
                case MultipleLicenseUsage.AllowForSameUser:
                    if (Environment.UserName == clientDiscoveredEventArgs.UserName)
                        return;
                    break;
            }

            RaiseLicenseInvalidated();
            RaiseMultipleLicenseDiscovered(clientDiscoveredEventArgs);
        }

        /// <summary>
        /// Validates loaded license
        /// </summary>
        public virtual void AssertValidLicense()
        {
            LicenseAttributes.Clear();
            if (HasExistingLicense())
            {
                if (DiscoveryEnabled)
                {
                    discoveryClient = new DiscoveryClient(senderId, UserId, Environment.MachineName, Environment.UserName);
                    discoveryClient.PublishMyPresence();
                }

                return;
            }

            Log.WarnFormat("[Licensing] Could not validate existing license\r\n{0}", License);
            throw new LicenseNotFoundException("Could not validate existing license");
        }

        private bool HasExistingLicense()
        {
            try
            {
                if (TryLoadingLicenseValuesFromValidatedXml() == false)
                {
                    Log.WarnFormat("[Licensing] Failed validating license:\r\n{0}", License);
                    return false;
                }
                Log.DebugFormat("[Licensing] License expiration date is {0}", ExpirationDate);

                bool result;
                if (LicenseType == LicenseType.Subscription ||
                    LicenseType == LicenseType.Professional ||
                    LicenseType == LicenseType.ManagedServiceProvider ||
                    LicenseType == LicenseType.Architect ||
                    LicenseType == LicenseType.Business ||
                    LicenseType == LicenseType.Enterprise ||
                    LicenseType == LicenseType.Education ||
                    LicenseType == LicenseType.Trial
                    )
                {
                    result = ValidateSubscription();
                }
                else
                {
                    result = DateTime.UtcNow < ExpirationDate;
                }

                if (result &&
                    !DisableTimeServersCheck)
                {
                    ValidateUsingNetworkTime();
                }

                if (!result)
                {
                    // If we don't disable it here and we throw an exception, if we're not done before the timer fires again we will cause an unhandled LicenseExpiredException.
                    DisableFutureChecks();

                    if (LicenseExpired == null) throw new LicenseExpiredException("Expiration Date : " + ExpirationDate);

                    LicenseExpired(ExpirationDate);
                }

                return true;
            }
            catch (RhinoLicensingException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region Subscription Licenses

        /// <summary>
        /// Gets or Sets the endpoint address of the subscription service
        /// </summary>
        public string SubscriptionEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the optional subscription endpoint passcode.
        /// </summary>
        /// <value>
        /// The optional subscription endpoint passcode.
        /// </value>
        public string SubscriptionEndpointPasscode { get; set; }

        /// <summary>
        /// Gets or sets the suscription end point check days near expiration.
        /// </summary>
        /// <value>
        /// The number of days before expiration that the subscription starts checking in to see if there is a new license.
        /// </value>
        public int SuscriptionEndPointCheckDaysNearExpiration
        {
            get { return subscriptionCheckDays; }
            set { subscriptionCheckDays = value; }
        }

        private bool ValidateSubscription()
        {
            //don't check until x number of days before the license expires
            if ((ExpirationDate - DateTime.UtcNow).TotalDays > SuscriptionEndPointCheckDaysNearExpiration) return true;

            if (currentlyValidatingSubscriptionLicense) return DateTime.UtcNow < ExpirationDate;

            // this information may not be available
            if (SubscriptionEndpoint == null)
            {
                Log.WarnFormat("This license is a subscription but does not have an endpoint configured to check into.");
                return ValidateWithoutUsingSubscriptionLeasing();
            }

            try
            {
                TryGettingNewLeaseSubscription();
            }
            catch (Exception e)
            {
                Log.WarnFormat("[Licensing] Unable to update subscription license. Details:{0} {1}", Environment.NewLine, e.Message);
            }

            return ValidateWithoutUsingSubscriptionLeasing();
        }

        private bool ValidateWithoutUsingSubscriptionLeasing()
        {
            currentlyValidatingSubscriptionLicense = true;
            try
            {
                return HasExistingLicense();
            }
            finally
            {
                currentlyValidatingSubscriptionLicense = false;
            }
        }

        private void TryGettingNewLeaseSubscription()
        {
            var channelFactory = new ChannelFactory<ISubscriptionLicensingService>(new BasicHttpBinding(), new EndpointAddress(SubscriptionEndpoint));
            var service = channelFactory.CreateChannel();
            try
            {
                var newLicense = service.LeaseLicense(License, SubscriptionEndpointPasscode);

                if (string.IsNullOrWhiteSpace(newLicense)) return;

                if (newLicense.IndexOf("no update", 0, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log.WarnFormat(newLicense);

                    return;
                }

                TryOverwritingWithNewLicense(newLicense);
            }
            finally
            {
                var communicationObject = service as ICommunicationObject;
                if (communicationObject != null)
                {
                    try
                    {
                        communicationObject.Close(TimeSpan.FromMilliseconds(200));
                    }
                    catch
                    {
                        communicationObject.Abort();
                    }
                }
            }
        }

        /// <summary>
        /// Loads the license file.
        /// </summary>
        /// <param name="newLicense"></param>
        /// <returns></returns>
        protected bool TryOverwritingWithNewLicense(string newLicense)
        {
            if (string.IsNullOrEmpty(newLicense)) return false;

            try
            {
                var xmlDocument = new XmlDocument();
                xmlDocument.LoadXml(newLicense);
            }
            catch (Exception e)
            {
                Log.Error("[Licensing] New license is not valid XML\r\n" + newLicense, e);
                return false;
            }
            // this attempts to write the file in License Validator
            License = newLicense;

            return true;
        }

        #endregion

        private void ValidateUsingNetworkTime()
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
                return;

            if (LicenseType == LicenseType.Business
                || LicenseType == LicenseType.Architect
                || LicenseType == LicenseType.Education
                || LicenseType == LicenseType.Enterprise
                || LicenseType == LicenseType.Trial
                )
            {
                // Many organizations have blocked NTP traffic, so this
                // check creates additional noise that is cause for
                // concern on enterprise security teams.
                // Since the traffic is already blocked,
                // this check would not produce a desired result
                // anyway.
                return;
            }

            var sntp = new SntpClient(GetTimeServers());
            sntp.BeginGetDate(time =>
            {
                if (time > ExpirationDate)
                    RaiseLicenseInvalidated();
            },
            () =>
            {
                /* ignored */
            });
        }

        /// <summary>
        /// Extension point to return different time servers
        /// </summary>
        /// <returns></returns>
        protected virtual string[] GetTimeServers()
        {
            return TimeServers;
        }

        /// <summary>
        /// Removes existing license from the machine.
        /// </summary>
        public virtual void RemoveExistingLicense()
        {
        }

        /// <summary>
        /// Loads license data from validated license file.
        /// </summary>
        /// <returns></returns>
        public bool TryLoadingLicenseValuesFromValidatedXml()
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(License);

                if (TryGetValidDocument(publicKey, doc) == false)
                {
                    Log.WarnFormat("[Licensing] Could not validate xml signature of:\r\n{0}", License);
                    return false;
                }

                if (doc.FirstChild == null)
                {
                    Log.WarnFormat("[Licensing] Could not find first child of:\r\n{0}", License);
                    return false;
                }

                if (doc.SelectSingleNode("/floating-license") != null)
                {
                    var node = doc.SelectSingleNode("/floating-license/license-server-public-key/text()");
                    if (node == null)
                    {
                        Log.WarnFormat("[Licensing] Invalid license, floating license without license server public key:\r\n{0}", License);
                        throw new InvalidOperationException(
                            "Invalid license file format, floating license without license server public key");
                    }
                    return ValidateFloatingLicense(node.InnerText);
                }

                var result = ValidateXmlDocumentLicense(doc);
                if (result && disableFutureChecks == false)
                {
                    nextLeaseTimer.Change(LeaseTimeout, LeaseTimeout);
                }

                return result;
            }
            catch (RhinoLicensingException)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Error("[Licensing] Could not validate license", e);
                return false;
            }
        }

        #region Floating Licenses

        /// <summary>
        /// Gets or Sets Floating license support
        /// </summary>
        public bool DisableFloatingLicenses { get; set; }

        private bool ValidateFloatingLicense(string publicKeyOfFloatingLicense)
        {
            if (DisableFloatingLicenses)
            {
                Log.Warn("[Licensing] Floating licenses have been disabled");
                return false;
            }
            if (licenseServerUrl == null)
            {
                Log.Warn("[Licensing] Could not find license server url");
                throw new InvalidOperationException("Floating license encountered, but licenseServerUrl was not set");
            }

            var success = false;
            var channelFactory = new ChannelFactory<ILicensingService>(new BasicHttpBinding(), new EndpointAddress(licenseServerUrl));
            var licensingService = channelFactory.CreateChannel();
            try
            {
                var leasedLicense = licensingService.LeaseLicense(
                    Environment.MachineName,
                    Environment.UserName,
                    clientId);
                ((ICommunicationObject)licensingService).Close();
                success = true;
                if (leasedLicense == null)
                {
                    Log.WarnFormat("[Licensing] Null response from license server: {0}", licenseServerUrl);
                    throw new FloatingLicenseNotAvailableException();
                }

                var doc = new XmlDocument();
                doc.LoadXml(leasedLicense);

                if (TryGetValidDocument(publicKeyOfFloatingLicense, doc) == false)
                {
                    Log.WarnFormat("[Licensing] Could not get valid license from floating license server {0}", licenseServerUrl);
                    throw new FloatingLicenseNotAvailableException();
                }

                var validLicense = ValidateXmlDocumentLicense(doc);
                if (validLicense)
                {
                    //setup next lease
                    var time = (ExpirationDate.AddMinutes(-5) - DateTime.UtcNow);
                    Log.DebugFormat("[Licensing] Will lease license again at {0}", time);
                    if (disableFutureChecks == false)
                        nextLeaseTimer.Change(time, time);
                }
                return validLicense;
            }
            finally
            {
                if (success == false)
                    ((ICommunicationObject)licensingService).Abort();
            }
        }

        #endregion

        internal bool ValidateXmlDocumentLicense(XmlDocument doc)
        {
            var id = doc.SelectSingleNode("/license/@id");
            if (id == null)
            {
                Log.WarnFormat("[Licensing] Could not find id attribute in license:\r\n{0}", License);
                return false;
            }

            UserId = new Guid(id.Value);

            var date = doc.SelectSingleNode("/license/@expiration");
            if (date == null)
            {
                Log.WarnFormat("[Licensing] Could not find expiration in license:\r\n{0}", License);
                return false;
            }

            ExpirationDate = DateTime.ParseExact(date.Value, "yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

            var licenseType = doc.SelectSingleNode("/license/@type");
            if (licenseType == null)
            {
                Log.WarnFormat("[Licensing] Could not find license type in {0}", licenseType);
                return false;
            }

            LicenseType = (LicenseType)Enum.Parse(typeof(LicenseType), licenseType.Value);

            var name = doc.SelectSingleNode("/license/name/text()");
            if (name == null)
            {
                Log.WarnFormat("[Licensing] Could not find licensee's name in license:\r\n{0}", License);
                return false;
            }

            Name = name.Value;

            var license = doc.SelectSingleNode("/license");
            foreach (XmlAttribute attrib in license.Attributes)
            {
                if (attrib.Name == "type" || attrib.Name == "expiration" || attrib.Name == "id")
                    continue;

                LicenseAttributes[attrib.Name] = attrib.Value;
            }

            return true;
        }

        private bool TryGetValidDocument(string licensePublicKey, XmlDocument doc)
        {
            var rsa = new RSACryptoServiceProvider();
            RSAKeyExtensions.FromXmlString(rsa,licensePublicKey);

            var nsMgr = new XmlNamespaceManager(doc.NameTable);
            nsMgr.AddNamespace("sig", "http://www.w3.org/2000/09/xmldsig#");

            var signedXml = new SignedXml(doc);
            var sig = (XmlElement)doc.SelectSingleNode("//sig:Signature", nsMgr);
            if (sig == null)
            {
                Log.WarnFormat("[Licensing] Could not find this signature node on license:\r\n{0}", License);
                return false;
            }
            signedXml.LoadXml(sig);

            return signedXml.CheckSignature(rsa);
        }

        /// <summary>
        /// Disables further license checks for the session.
        /// </summary>
        public void DisableFutureChecks()
        {
            disableFutureChecks = true;
            nextLeaseTimer.Change(Timeout.Infinite, Timeout.Infinite);
            nextLeaseTimer.Dispose();
        }

        /// <summary>
        /// Options for detecting multiple licenses
        /// </summary>
        public enum MultipleLicenseUsage
        {
            /// <summary>
            /// Deny if multiple licenses are used
            /// </summary>
            Deny,
            /// <summary>
            /// Only allow if it is running for the same user
            /// </summary>
            AllowForSameUser
        }
    }
}
