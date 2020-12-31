// ============================================================================
// FileName: RegistrarCore.cs
//
// Description:
// RFC3822 compliant SIP Registrar.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 21 Jan 2006	Aaron Clauson	Created.
// 22 Nov 2007  Aaron Clauson   Fixed bug where binding refresh was generating a 
//                              duplicate exception if the uac endpoint changed but the contact did not.
// 29 Dec 2020  Aaron Clauson   Added to server project.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;
using SIPAspNetServer.DataAccess;

namespace SIPAspNetServer
{
    public enum RegisterResultEnum
    {
        Unknown = 0,
        Trying = 1,
        Forbidden = 2,
        Authenticated = 3,
        AuthenticationRequired = 4,
        Failed = 5,
        Error = 6,
        RequestWithNoUser = 7,
        RemoveAllRegistrations = 9,
        DuplicateRequest = 10,
        AuthenticatedFromCache = 11,
        RequestWithNoContact = 12,
        NonRegisterMethod = 13,
        DomainNotServiced = 14,
        IntervalTooBrief = 15,
        SwitchboardPaymentRequired = 16,
    }

    /// <summary>
    /// The registrar core is the class that actually does the work of receiving registration requests and populating and
    /// maintaining the SIP registrations list.
    /// 
    /// From RFC 3261 Chapter "10.2 Constructing the REGISTER Request"
    /// - Request-URI: The Request-URI names the domain of the location service for which the registration is meant.
    /// - The To header field contains the address of record whose registration is to be created, queried, or modified.  
    ///   The To header field and the Request-URI field typically differ, as the former contains a user name. 
    /// 
    /// [ed Therefore:
    /// - The Request-URI inidcates the domain for the registration and should match the domain in the To address of record.
    /// - The To address of record contians the username of the user that is attempting to authenticate the request.]
    /// 
    /// Method of operation:
    ///  - New SIP messages received by the SIP Transport layer and queued before being sent to RegistrarCode for processing. For requests
    ///    or response that match an existing REGISTER transaction the SIP Transport layer will handle the retransmit or drop the request if
    ///    it's already being processed.
    ///  - Any non-REGISTER requests received by the RegistrarCore are responded to with not supported,
    ///  - If a persistence is being used to store registered contacts there will generally be a number of threads running for the
    ///    persistence class. Of those threads there will be one that runs calling the SIPRegistrations.IdentifyDirtyContacts. This call identifies
    ///    expired contacts and initiates the sending of any keep alive and OPTIONs requests.
    /// </summary>
    public class RegistrarCore
    {
        private const int MAX_REGISTER_QUEUE_SIZE = 1000;
        private const int MAX_PROCESS_REGISTER_SLEEP = 10000;
        private const string REGISTRAR_THREAD_NAME_PREFIX = "sipregistrar-core";

        private readonly ILogger Logger = SIPSorcery.LogFactory.CreateLogger<RegistrarCore>();

        private int m_minimumBindingExpiry = SIPRegistrarBindingsManager.MINIMUM_EXPIRY_SECONDS;

        private SIPTransport m_sipTransport;
        private SIPRegistrarBindingsManager m_registrarBindingsManager;
        private SIPAccountDataLayer m_sipAccountsDataLayer;
        private SIPRegistrarBindingDataLayer m_SIPRegistrarBindingDataLayer;
        private SIPDomainDataLayer m_sipDomainDataLayer;

        private string m_serverAgent = SIPConstants.SIP_VERSION_STRING;
        private bool m_mangleUACContact = false;            // Whether or not to adjust contact URIs that contain private hosts to the value of the bottom via received socket.
        private bool m_strictRealmHandling = false;         // If true the registrar will only accept registration requests for domains it is configured for, otherwise any realm is accepted.
        private ConcurrentQueue<SIPNonInviteTransaction> m_registerQueue = new ConcurrentQueue<SIPNonInviteTransaction>();
        private AutoResetEvent m_registerARE = new AutoResetEvent(false);

        public event Action<double, bool> RegisterComplete;     // Event to allow hook into get notifications about the processing time for registrations. The boolean parameter is true of the request contained an authentication header.

        public int BacklogLength
        {
            get { return m_registerQueue.Count; }
        }

        public bool Stop;

        public RegistrarCore(
            SIPTransport sipTransport,
            bool mangleUACContact,
            bool strictRealmHandling)
        {
            m_sipTransport = sipTransport;
            m_mangleUACContact = mangleUACContact;
            m_strictRealmHandling = strictRealmHandling;

            m_registrarBindingsManager = new SIPRegistrarBindingsManager();
            m_sipAccountsDataLayer = new SIPAccountDataLayer();
            m_SIPRegistrarBindingDataLayer = new SIPRegistrarBindingDataLayer();
            m_sipDomainDataLayer = new SIPDomainDataLayer();
        }

        public void Start(int threadCount)
        {
            Logger.LogInformation($"SIPRegistrarCore starting with {threadCount} threads.");

            for (int index = 1; index <= threadCount; index++)
            {
                string threadSuffix = index.ToString();
                ThreadPool.QueueUserWorkItem(delegate { ProcessRegisterRequest(REGISTRAR_THREAD_NAME_PREFIX + threadSuffix); });
            }
        }

        public void AddRegisterRequest(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest registerRequest)
        {
            try
            {
                if (registerRequest.Method != SIPMethodsEnum.REGISTER)
                {
                    SIPResponse notSupportedResponse = GetErrorResponse(registerRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, "Registration requests only");
                    m_sipTransport.SendResponseAsync(notSupportedResponse).Wait();
                }
                else
                {
                    int requestedExpiry = GetRequestedExpiry(registerRequest);

                    if (registerRequest.Header.To == null)
                    {
                        Logger.LogDebug("Bad register request, no To header from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPResponse.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing To header");
                        m_sipTransport.SendResponseAsync(badReqResponse).Wait();
                    }
                    else if (registerRequest.Header.To.ToURI.User.IsNullOrBlank())
                    {
                        Logger.LogDebug("Bad register request, no To user from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPResponse.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing username on To header");
                        m_sipTransport.SendResponseAsync(badReqResponse).Wait();
                    }
                    else if (registerRequest.Header.Contact == null || registerRequest.Header.Contact.Count == 0)
                    {
                        Logger.LogDebug("Bad register request, no Contact header from " + remoteEndPoint + ".");
                        SIPResponse badReqResponse = SIPResponse.GetResponse(registerRequest, SIPResponseStatusCodesEnum.BadRequest, "Missing Contact header");
                        m_sipTransport.SendResponseAsync(badReqResponse).Wait();
                    }
                    else if (requestedExpiry > 0 && requestedExpiry < m_minimumBindingExpiry)
                    {
                        Logger.LogDebug("Bad register request, no expiry of " + requestedExpiry + " to small from " + remoteEndPoint + ".");
                        SIPResponse tooFrequentResponse = GetErrorResponse(registerRequest, SIPResponseStatusCodesEnum.IntervalTooBrief, null);
                        tooFrequentResponse.Header.MinExpires = m_minimumBindingExpiry;
                        m_sipTransport.SendResponseAsync(tooFrequentResponse).Wait();
                    }
                    else
                    {
                        if (m_registerQueue.Count < MAX_REGISTER_QUEUE_SIZE)
                        {
                            SIPNonInviteTransaction registrarTransaction = new SIPNonInviteTransaction(m_sipTransport, registerRequest, null);
                            m_registerQueue.Enqueue(registrarTransaction);
                        }
                        else
                        {
                            Logger.LogError("Register queue exceeded max queue size " + MAX_REGISTER_QUEUE_SIZE + ", overloaded response sent.");
                            SIPResponse overloadedResponse = SIPResponse.GetResponse(registerRequest, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Registrar overloaded, please try again shortly");
                            m_sipTransport.SendResponseAsync(overloadedResponse).Wait();
                        }

                        m_registerARE.Set();
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception AddRegisterRequest (" + remoteEndPoint.ToString() + "). " + excp.Message);
            }
        }

        private void ProcessRegisterRequest(string threadName)
        {
            try
            {
                Thread.CurrentThread.Name = threadName;

                while (!Stop)
                {
                    if (m_registerQueue.Count > 0)
                    {
                        try
                        {
                            if (m_registerQueue.TryDequeue(out var registrarTransaction))
                            {
                                DateTime startTime = DateTime.Now;
                                RegisterResultEnum result = Register(registrarTransaction);
                                TimeSpan duration = DateTime.Now.Subtract(startTime);

                                if (RegisterComplete != null)
                                {
                                    RegisterComplete(duration.TotalMilliseconds, registrarTransaction.TransactionRequest.Header.AuthenticationHeader != null);
                                }
                            }
                        }
                        catch (Exception regExcp)
                        {
                            Logger.LogError("Exception ProcessRegisterRequest Register Job. " + regExcp.Message);
                        }
                    }
                    else
                    {
                        m_registerARE.WaitOne(MAX_PROCESS_REGISTER_SLEEP);
                    }
                }

                Logger.LogWarning("ProcessRegisterRequest thread " + Thread.CurrentThread.Name + " stopping.");
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception ProcessRegisterRequest (" + Thread.CurrentThread.Name + "). " + excp);
            }
        }

        private int GetRequestedExpiry(SIPRequest registerRequest)
        {
            int contactHeaderExpiry = (registerRequest.Header.Contact != null && registerRequest.Header.Contact.Count > 0) ? registerRequest.Header.Contact[0].Expires : -1;
            return (contactHeaderExpiry == -1) ? registerRequest.Header.Expires : contactHeaderExpiry;
        }

        private RegisterResultEnum Register(SIPNonInviteTransaction registerTransaction)
        {
            try
            {
                SIPRequest sipRequest = registerTransaction.TransactionRequest;
                SIPURI registerURI = sipRequest.URI;
                SIPToHeader toHeader = sipRequest.Header.To;
                string toUser = toHeader.ToURI.User;
                string canonicalDomain = (!m_strictRealmHandling) ? m_sipDomainDataLayer.GetCanonicalDomain(toHeader.ToURI.Host, true) : toHeader.ToURI.Host;
                int requestedExpiry = GetRequestedExpiry(sipRequest);

                if (canonicalDomain == null)
                {
                    Logger.LogWarning("Register request for " + toHeader.ToURI.Host + " rejected as no matching domain found.");
                    SIPResponse noDomainResponse = GetErrorResponse(sipRequest, SIPResponseStatusCodesEnum.Forbidden, "Domain not serviced");
                    registerTransaction.SendResponse(noDomainResponse);
                    return RegisterResultEnum.DomainNotServiced;
                }
                else
                {
                    //SIPAccount sipAccount = m_sipAssetsCtx.SIPAccounts.Where(s => s.SIPUsername == toUser && s.Sipdomain == canonicalDomain).SingleOrDefault();
                    SIPAccount sipAccount = m_sipAccountsDataLayer.GetSIPAccount(toUser, canonicalDomain);

                    if (sipAccount == null)
                    {
                        Logger.LogWarning($"RegistrarCore SIP account {toUser}@{canonicalDomain} does not exist.");
                        SIPResponse forbiddenResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Forbidden, null);
                        registerTransaction.SendResponse(forbiddenResponse);
                        return RegisterResultEnum.Forbidden;
                    }
                    else
                    {
                        SIPRequestAuthenticationResult authenticationResult = SIPRequestAuthenticator.AuthenticateSIPRequest(registerTransaction.TransactionRequest.LocalSIPEndPoint, registerTransaction.TransactionRequest.RemoteSIPEndPoint, sipRequest, sipAccount);

                        if (!authenticationResult.Authenticated)
                        {
                            // 401 Response with a fresh nonce needs to be sent.
                            SIPResponse authReqdResponse = SIPResponse.GetResponse(sipRequest, authenticationResult.ErrorResponse, null);
                            authReqdResponse.Header.AuthenticationHeader = authenticationResult.AuthenticationRequiredHeader;
                            registerTransaction.SendResponse(authReqdResponse);

                            if (authenticationResult.ErrorResponse == SIPResponseStatusCodesEnum.Forbidden)
                            {
                                Logger.LogWarning("Forbidden " + toUser + "@" + canonicalDomain + " does not exist, " + sipRequest.Header.ProxyReceivedFrom + ", " + sipRequest.Header.UserAgent + ".");
                                return RegisterResultEnum.Forbidden;
                            }
                            else
                            {
                                return RegisterResultEnum.AuthenticationRequired;
                            }
                        }
                        else
                        {
                            if (sipRequest.Header.Contact == null || sipRequest.Header.Contact.Count == 0)
                            {
                                // No contacts header to update bindings with, return a list of the current bindings.
                                //List<SIPRegistrarBinding> bindings = m_registrarBindingsManager.GetBindings(sipAccount.ID);
                                List<SIPRegistrarBinding> bindings = m_SIPRegistrarBindingDataLayer.GetForSIPAccount(sipAccount.ID).ToList();
                                //List<SIPContactHeader> contactsList = m_registrarBindingsManager.GetContactHeader(); // registration.GetContactHeader(true, null);
                                if (bindings != null)
                                {
                                    sipRequest.Header.Contact = GetContactHeader(bindings);
                                }

                                SIPResponse okResponse = GetOkResponse(sipRequest);
                                registerTransaction.SendResponse(okResponse);
                                Logger.LogDebug("Empty registration request successful for " + toUser + "@" + canonicalDomain + " from " + sipRequest.Header.ProxyReceivedFrom + ".");
                            }
                            else
                            {
                                SIPEndPoint uacRemoteEndPoint = SIPEndPoint.TryParse(sipRequest.Header.ProxyReceivedFrom) ?? registerTransaction.TransactionRequest.RemoteSIPEndPoint;
                                SIPEndPoint proxySIPEndPoint = SIPEndPoint.TryParse(sipRequest.Header.ProxyReceivedOn);
                                SIPEndPoint registrarEndPoint = registerTransaction.TransactionRequest.LocalSIPEndPoint;

                                SIPResponseStatusCodesEnum updateResult = SIPResponseStatusCodesEnum.Ok;
                                string updateMessage = null;

                                DateTime startTime = DateTime.Now;

                                List<SIPRegistrarBinding> bindingsList = m_registrarBindingsManager.UpdateBindings(
                                    sipAccount,
                                    proxySIPEndPoint,
                                    uacRemoteEndPoint,
                                    registrarEndPoint,
                                    //sipRequest.Header.Contact[0].ContactURI.CopyOf(),
                                    sipRequest.Header.Contact,
                                    sipRequest.Header.CallId,
                                    sipRequest.Header.CSeq,
                                    //sipRequest.Header.Contact[0].Expires,
                                    sipRequest.Header.Expires,
                                    sipRequest.Header.UserAgent,
                                    out updateResult,
                                    out updateMessage);

                                //int bindingExpiry = GetBindingExpiry(bindingsList, sipRequest.Header.Contact[0].ContactURI.ToString());
                                TimeSpan duration = DateTime.Now.Subtract(startTime);
                                Logger.LogDebug("Binding update time for " + toUser + "@" + canonicalDomain + " took " + duration.TotalMilliseconds + "ms.");

                                if (updateResult == SIPResponseStatusCodesEnum.Ok)
                                {
                                    string proxySocketStr = (proxySIPEndPoint != null) ? " (proxy=" + proxySIPEndPoint.ToString() + ")" : null;

                                    int bindingCount = 1;
                                    foreach (SIPRegistrarBinding binding in bindingsList)
                                    {
                                        string bindingIndex = (bindingsList.Count == 1) ? String.Empty : " (" + bindingCount + ")";
                                        //FireProxyLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Registrar, SIPMonitorEventTypesEnum.RegisterSuccess, "Registration successful for " + toUser + "@" + canonicalDomain + " from " + uacRemoteEndPoint + proxySocketStr + ", binding " + binding.ContactSIPURI.ToParameterlessString() + ";expiry=" + binding.Expiry + bindingIndex + ".", toUser));
                                        Logger.LogDebug("Registration successful for " + toUser + "@" + canonicalDomain + " from " + uacRemoteEndPoint + ", binding " + binding.ContactURI + ", expiry " + binding.Expiry + bindingIndex + ".");
                                        //FireProxyLogEvent(new SIPMonitorMachineEvent(SIPMonitorMachineEventTypesEnum.SIPRegistrarBindingUpdate, toUser, uacRemoteEndPoint, sipAccount.Id.ToString()));
                                        bindingCount++;
                                    }

                                    sipRequest.Header.Contact = GetContactHeader(bindingsList);
                                    SIPResponse okResponse = GetOkResponse(sipRequest);

                                    // If a request was made for a switchboard token and a certificate is available to sign the tokens then generate it.
                                    //if (sipRequest.Header.SwitchboardTokenRequest > 0 && m_switchbboardRSAProvider != null)
                                    //{
                                    //    SwitchboardToken token = new SwitchboardToken(sipRequest.Header.SwitchboardTokenRequest, sipAccount.Owner, uacRemoteEndPoint.Address.ToString());

                                    //    lock (m_switchbboardRSAProvider)
                                    //    {
                                    //        token.SignedHash = Convert.ToBase64String(m_switchbboardRSAProvider.SignHash(Crypto.GetSHAHash(token.GetHashString()), null));
                                    //    }

                                    //    string tokenXML = token.ToXML(true);
                                    //    logger.Debug("Switchboard token set for " + sipAccount.Owner + " with expiry of " + token.Expiry + "s.");
                                    //    okResponse.Header.SwitchboardToken = Crypto.SymmetricEncrypt(sipAccount.SIPPassword, sipRequest.Header.AuthenticationHeader.SIPDigest.Nonce, tokenXML);
                                    //}

                                    registerTransaction.SendResponse(okResponse);
                                }
                                else
                                {
                                    // The binding update failed even though the REGISTER request was authorised. This is probably due to a 
                                    // temporary problem connecting to the bindings data store. Send Ok but set the binding expiry to the minimum so
                                    // that the UA will try again as soon as possible.
                                    Logger.LogError("Registration request successful but binding update failed for " + toUser + "@" + canonicalDomain + " from " + registerTransaction.TransactionRequest.RemoteSIPEndPoint + ".");
                                    sipRequest.Header.Contact[0].Expires = m_minimumBindingExpiry;
                                    SIPResponse okResponse = GetOkResponse(sipRequest);
                                    registerTransaction.SendResponse(okResponse);
                                }
                            }

                            return RegisterResultEnum.Authenticated;
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                string regErrorMessage = "Exception registrarcore registering. " + excp.Message + "\r\n" + registerTransaction.TransactionRequest.ToString();
                Logger.LogError(regErrorMessage);

                try
                {
                    SIPResponse errorResponse = GetErrorResponse(registerTransaction.TransactionRequest, SIPResponseStatusCodesEnum.InternalServerError, null);
                    registerTransaction.SendResponse(errorResponse);
                }
                catch { }

                return RegisterResultEnum.Error;
            }
        }

        private int GetBindingExpiry(List<SIPRegistrarBinding> bindings, string bindingURI)
        {
            if (bindings == null || bindings.Count == 0)
            {
                return -1;
            }
            else
            {
                foreach (SIPRegistrarBinding binding in bindings)
                {
                    if (binding.ContactURI == bindingURI)
                    {
                        return binding.Expiry;
                    }
                }
                return -1;
            }
        }

        /// <summary>
        /// Gets a SIP contact header for this address-of-record based on the bindings list.
        /// </summary>
        /// <returns></returns>
        private List<SIPContactHeader> GetContactHeader(List<SIPRegistrarBinding> bindings)
        {
            if (bindings != null && bindings.Count > 0)
            {
                List<SIPContactHeader> contactHeaderList = new List<SIPContactHeader>();

                foreach (SIPRegistrarBinding binding in bindings)
                {
                    SIPContactHeader bindingContact = new SIPContactHeader(null, SIPURI.ParseSIPURIRelaxed(binding.ContactURI));
                    bindingContact.Expires = Convert.ToInt32(binding.ExpiryTime.Subtract(DateTime.UtcNow).TotalSeconds % Int32.MaxValue);
                    contactHeaderList.Add(bindingContact);
                }

                return contactHeaderList;
            }
            else
            {
                return null;
            }
        }

        private SIPResponse GetOkResponse(SIPRequest sipRequest)
        {
            try
            {
                SIPResponse okResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                SIPHeader requestHeader = sipRequest.Header;
                okResponse.Header = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                // RFC3261 has a To Tag on the example in section "24.1 Registration".
                if (okResponse.Header.To.ToTag == null || okResponse.Header.To.ToTag.Trim().Length == 0)
                {
                    okResponse.Header.To.ToTag = CallProperties.CreateNewTag();
                }

                okResponse.Header.CSeqMethod = requestHeader.CSeqMethod;
                okResponse.Header.Vias = requestHeader.Vias;
                okResponse.Header.Server = m_serverAgent;
                okResponse.Header.MaxForwards = Int32.MinValue;
                okResponse.Header.SetDateHeader();

                return okResponse;
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception GetOkResponse. " + excp.Message);
                throw;
            }
        }

        private SIPResponse GetAuthReqdResponse(SIPRequest sipRequest, string nonce, string realm)
        {
            try
            {
                SIPResponse authReqdResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Unauthorised, null);
                SIPAuthenticationHeader authHeader = new SIPAuthenticationHeader(SIPAuthorisationHeadersEnum.WWWAuthenticate, realm, nonce);
                SIPHeader requestHeader = sipRequest.Header;
                SIPHeader unauthHeader = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                if (unauthHeader.To.ToTag == null || unauthHeader.To.ToTag.Trim().Length == 0)
                {
                    unauthHeader.To.ToTag = CallProperties.CreateNewTag();
                }

                unauthHeader.CSeqMethod = requestHeader.CSeqMethod;
                unauthHeader.Vias = requestHeader.Vias;
                unauthHeader.AuthenticationHeader = authHeader;
                unauthHeader.Server = m_serverAgent;
                unauthHeader.MaxForwards = Int32.MinValue;

                authReqdResponse.Header = unauthHeader;

                return authReqdResponse;
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception GetAuthReqdResponse. " + excp.Message);
                throw;
            }
        }

        private SIPResponse GetErrorResponse(SIPRequest sipRequest, SIPResponseStatusCodesEnum errorResponseCode, string errorMessage)
        {
            try
            {
                SIPResponse errorResponse = SIPResponse.GetResponse(sipRequest, errorResponseCode, null);
                if (errorMessage != null)
                {
                    errorResponse.ReasonPhrase = errorMessage;
                }

                SIPHeader requestHeader = sipRequest.Header;
                SIPHeader errorHeader = new SIPHeader(requestHeader.Contact, requestHeader.From, requestHeader.To, requestHeader.CSeq, requestHeader.CallId);

                if (errorHeader.To.ToTag == null || errorHeader.To.ToTag.Trim().Length == 0)
                {
                    errorHeader.To.ToTag = CallProperties.CreateNewTag();
                }

                errorHeader.CSeqMethod = requestHeader.CSeqMethod;
                errorHeader.Vias = requestHeader.Vias;
                errorHeader.Server = m_serverAgent;
                errorHeader.MaxForwards = Int32.MinValue;

                errorResponse.Header = errorHeader;

                return errorResponse;
            }
            catch (Exception excp)
            {
                Logger.LogError("Exception GetErrorResponse. " + excp.Message);
                throw;
            }
        }
    }
}