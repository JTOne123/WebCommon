﻿using System;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using System.Linq;
using Certes.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Newtonsoft.Json;
using LagoVista.Net.LetsEncrypt.AcmeServices.Interfaces;
using LagoVista.Net.LetsEncrypt.Models;
using LagoVista.Net.LetsEncrypt.Interfaces;
using Certes.Acme.Resource;
using System.Net.Http;
using LagoVista.IoT.Logging.Loggers;
using LagoVista.Core;

namespace LagoVista.Net.LetsEncrypt.AcmeServices
{
    public class AcmeCertificateManager : ICertificateManager
    {
        readonly IAcmeSettings _settings;
        readonly ICertStorage _storage;
        IInstanceLogger _instanceLogger;

        private const string Tag = "AcmeCertMgr";

        public AcmeCertificateManager(ICertStorage storage, IAcmeSettings settings, IInstanceLogger instanceLogger)
        {
            _storage = storage;
            _settings = settings;
        }

        public async Task<X509Certificate2> GetCertificate(string domainName)
        {
            this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Certificate Requested for {domainName}");
            var pfx = await _storage.GetCertAsync(domainName + "X");
            if (pfx != null)
            {
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Certificate found in storage for {domainName}");
                var cert = new X509Certificate2(pfx, _settings.PfxPassword);
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Certificate has expire date of {cert.NotAfter}");
                if (cert.NotAfter - DateTime.UtcNow > _settings.RenewalPeriod)
                {
                    this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Certificate is valid, returning cert");
                    return cert;
                }
                else
                {
                    this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Certificate is will expire, will request new cert");
                }
            }
            else
            {
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Did not find certificate in storage for: {domainName}");
            }

            this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Requesting new certificate for {domainName}");
            pfx = await RequestNewCertificateV2(domainName);
            if (pfx != null)
            {
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Obtained certificate for {domainName}");
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Storing certificate for {domainName}");
                await _storage.StoreCertAsync(domainName, pfx);
                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_GetCertificate", $"Stored certificate will create X509 and return {domainName}");
                return new X509Certificate2(pfx, _settings.PfxPassword);
            }
            else
            {
                this._instanceLogger.AddError($"{Tag}_GetCertificate", $"Response from certificate is null for {domainName}, did not get certificate.");
                return null;
            }
        }

        private async Task<Order> PollResultAsync(AcmeContext context, IOrderContext order, Uri uri)
        {
            int attempt = 0;
            do
            {
                await Task.Delay(5000 * attempt);

                var authResult = await order.Resource();

                if (authResult.Status == OrderStatus.Ready)
                {
                    this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_PollResultAsync", $"Certificate is ready: {authResult.Status}.");
                    return authResult;
                }
                else
                {
                    this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_PollResultAsync", $"Waiting for certification creation: {authResult.Status}");
                }
            }
            while (++attempt < 5);

            return null;
        }

        private async Task<byte[]> RequestNewCertificateV2(string domainName)
        {
            var context = new AcmeContext(_settings.AcmeUri);
            await context.NewAccount(_settings.EmailAddress, true);

            var order = await context.NewOrder(new[] { domainName });
            var auths = await order.Authorizations();

            var authZ = auths.First();

            var httpChallenge = await authZ.Http();
            var key = httpChallenge.KeyAuthz;

            var challenge = httpChallenge.KeyAuthz.Split('.')[0];

            await _storage.SetChallengeAndResponseAsync(challenge, key);

            await httpChallenge.Validate();

            await PollResultAsync(context, order, order.Location);

            try
            {
                var privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                var cert = await order.Generate(new CsrInfo
                {
                    CountryName = "USA",
                    State = "FL",
                    Locality = "TAMPA",
                    Organization = "SOFTWARE LOGISTICS",
                    OrganizationUnit = "HOSTING",
                    CommonName = domainName,
                }, privateKey);

                var certPem = cert.ToPem();
                var pfxBuilder = cert.ToPfx(privateKey);
                var buffer = pfxBuilder.Build(domainName, _settings.PfxPassword);

                this._instanceLogger.AddCustomEvent(Core.PlatformSupport.LogLevel.Verbose, $"{Tag}_RequestNewCertificateV2", $"Created new certificate and returning byte array for {domainName}.");

                return buffer;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                this._instanceLogger.AddException($"{Tag}_RequestNewCertificateV2", ex, _settings.AcmeUri.ToString().ToKVP("acmeUri"), domainName.ToKVP("domainName"));
                Console.ResetColor();
                return null;
            }
        }

    }
}