namespace Sitecore.Support.EmailCampaign.Cd.sitecore_modules.Web.EXM
{
    using Microsoft.Extensions.DependencyInjection;
    using Sitecore;
    using Sitecore.Analytics;
    using Sitecore.Analytics.Pipelines.ExcludeRobots;
    using Sitecore.Common;
    using Sitecore.Configuration;
    using Sitecore.Data;
    using Sitecore.DependencyInjection;
    using Sitecore.EmailCampaign.Cd;
    using Sitecore.EmailCampaign.Cd.EmailEvents;
    using Sitecore.EmailCampaign.Cd.Pipelines.RedirectUrl;
    using Sitecore.EmailCampaign.Cd.Services;
    using Sitecore.EmailCampaign.Model.Messaging;
    using Sitecore.EmailCampaign.Model.Web.Exceptions;
    using Sitecore.EmailCampaign.Model.Web.Settings;
    using Sitecore.EmailCampaign.Model.XConnect.Events;
    using Sitecore.Modules.EmailCampaign.Core;
    using Sitecore.Modules.EmailCampaign.Core.Contacts;
    using Sitecore.Modules.EmailCampaign.Messages;
    using Sitecore.XConnect;
    using System;
    using System.Web;

    public class RedirectUrlPage : MessageEventPage
    {
        public const string PipelineName = "redirectUrl";

        private readonly TimeSpan _duplicateProtectionInterval;

        private readonly PipelineHelper _pipelineHelper;

        private readonly IClientApiService _clientApiService;

        private readonly IEmailEventStorage _emailEventStorage;

        private string _originalLink;

        public RedirectUrlPage()
            : this(ServiceLocator.ServiceProvider.GetService<IEmailEventStorage>(),
                TimeSpan.FromSeconds(int.Parse(Settings.GetSetting("EXM.DuplicateProtectionInterval", "300"))),
                ServiceLocator.ServiceProvider.GetService<PipelineHelper>(),
                ServiceLocator.ServiceProvider.GetService<IClientApiService>())
        {
        }

        internal RedirectUrlPage(IEmailEventStorage emailEventStorage, TimeSpan duplicateProtectionInterval,
            PipelineHelper pipelineHelper, IClientApiService clientApiService)
        {
            _emailEventStorage = emailEventStorage;
            _duplicateProtectionInterval = duplicateProtectionInterval;
            _pipelineHelper = pipelineHelper;
            _clientApiService = clientApiService;
        }

        protected override void HandleMessageEvent()
        {
            if (GlobalSettings.Enabled)
            {
                _originalLink = ExmContext.QueryString["ec_url"];
                if (string.IsNullOrEmpty(_originalLink))
                {
                    throw new EmailCampaignException(string.Format("The '{0}' query string parameter is empty.",
                        "ec_url"));
                }

                if (ExmContext.Message == null || ExmContext.MessageId == (ID) null)
                {
                    throw new EmailCampaignException("The context Message item is null.");
                }

                ExcludeRobotsArgs excludeRobotsArgs = new ExcludeRobotsArgs();
                ExcludeRobotsPipeline.Run(excludeRobotsArgs);
                if (ExmContext.Contact != null && !excludeRobotsArgs.IsInExcludeList)
                {
                    RegisterOpen(ExmContext.Message, ExmContext.ContactIdentifier,
                        new HttpContextWrapper(HttpContext.Current));
                }

                EmailClickedEvent emailEvent = new EmailClickedEvent
                {
                    MessageId = ExmContext.MessageId.Guid,
                    InstanceId = ExmContext.MessageId.Guid,
                    MessageLanguage = ExmContext.Message.TargetLanguage.Name,
                    TestValueIndex = ExmContext.Message.TestValueIndex,
                    EmailAddressHistoryEntryId = ExmContext.Message.EmailAddressHistoryEntryId
                };
                RedirectUrlPipelineArgs redirectUrlPipelineArgs = new RedirectUrlPipelineArgs(
                    new EventData(ExmContext.ContactIdentifier, emailEvent), ExmContext.QueryString, _originalLink,
                    Sitecore.Analytics.Tracker.IsActive);
                _pipelineHelper.RunPipeline("redirectUrl", redirectUrlPipelineArgs, "exm.messageEvents");
                string value;
                if (redirectUrlPipelineArgs.RedirectToUrl == null)
                {
                    Logger.LogWarn(string.Format("{0} returned empty RedirectToUrl, falling back to {1}", "redirectUrl",
                        _originalLink));
                    value = _originalLink;
                }
                else
                {
                    value = redirectUrlPipelineArgs.RedirectToUrl.ToString();
                }

                base.Response.Status = "301 Moved Permanently";
                base.Response.AddHeader("Location", value);
            }
        }

        private void RegisterOpen(MessageItem message, ContactIdentifier contactIdentifier, HttpContextBase context)
        {
            RegistrationResult registrationResult;
            try
            {
                registrationResult = _emailEventStorage.RegisterEmailOpened(message.MessageId.ToID(),
                    message.MessageId.ToID(), contactIdentifier, _duplicateProtectionInterval);
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to get a registration result", e);
                return;
            }

            if (registrationResult.IsDuplicate)
            {
                Logger.LogDebug(
                    $"Email opened registration not processed as open is found in cache and is within duplicate protection interval. Message Id: {message.MessageId}, instance id: {message.MessageId}, contact id: {contactIdentifier.ToLogFile()}");
                return;
            }

            EmailOpenMessage emailOpenMessage = new EmailOpenMessage
            {
                IPAddress = context.GetIpAddress(),
                UserAgent = context.Request.UserAgent,
                RequestRegistered = DateTime.UtcNow,
                MessageId = message.MessageId,
                ContactIdentifier = contactIdentifier,
                InstanceId = message.MessageId,
                SiteName = Sitecore.Context.GetSiteName(),
                TargetLanguage = message.TargetLanguage.Name,
                TestValueIndex = message.TestValueIndex,
                EmailAddressHistoryEntryId = message.EmailAddressHistoryEntryId
            };
            AddCustomData(emailOpenMessage, context);
            _clientApiService.RegisterEmailOpen(emailOpenMessage);
        }

        protected virtual void AddCustomData(EmailOpenMessage dto, HttpContextBase context)
        {
        }

        protected override void Failed()
        {
            Tracker.Current?.CurrentPage?.Cancel();
            base.Response.Status = "301 Moved Permanently";
            base.Response.AddHeader("Location", _originalLink ?? Settings.ItemNotFoundUrl);
        }
    }
}