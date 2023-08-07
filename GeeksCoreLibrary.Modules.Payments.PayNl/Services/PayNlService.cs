using System.Net;
using GeeksCoreLibrary.Components.OrderProcess.Models;
using GeeksCoreLibrary.Components.ShoppingBasket;
using GeeksCoreLibrary.Components.ShoppingBasket.Interfaces;
using GeeksCoreLibrary.Core.DependencyInjection.Interfaces;
using GeeksCoreLibrary.Core.Enums;
using GeeksCoreLibrary.Core.Extensions;
using GeeksCoreLibrary.Core.Helpers;
using GeeksCoreLibrary.Core.Models;
using GeeksCoreLibrary.Modules.Databases.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Enums;
using GeeksCoreLibrary.Modules.Payments.Interfaces;
using GeeksCoreLibrary.Modules.Payments.Models;
using GeeksCoreLibrary.Modules.Payments.PayNl.Models;
using GeeksCoreLibrary.Modules.Payments.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;
using Amount = GeeksCoreLibrary.Modules.Payments.PayNl.Models.Amount;
using PayNlConstants = GeeksCoreLibrary.Modules.Payments.PayNl.Models.Constants;
using Constants = GeeksCoreLibrary.Components.OrderProcess.Models.Constants;
using Integration = GeeksCoreLibrary.Modules.Payments.PayNl.Models.Integration;
using TransactionStartBody = GeeksCoreLibrary.Modules.Payments.PayNl.Models.TransactionStartBody;

namespace GeeksCoreLibrary.Modules.Payments.PayNl.Services;

/// <inheritdoc cref="IPaymentServiceProviderService" />
public class PayNlService : PaymentServiceProviderBaseService, IPaymentServiceProviderService, IScopedService
{
    private const string BaseUrl = "https://rest.pay.nl/";
    private readonly IDatabaseConnection databaseConnection;
    private readonly ILogger<PaymentServiceProviderBaseService> logger;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly GclSettings gclSettings;
    private readonly IShoppingBasketsService shoppingBasketsService;

    public PayNlService(
        IDatabaseHelpersService databaseHelpersService,
        IDatabaseConnection databaseConnection,
        ILogger<PaymentServiceProviderBaseService> logger,
        IOptions<GclSettings> gclSettings,
        IShoppingBasketsService shoppingBasketsService,
        IHttpContextAccessor httpContextAccessor = null) : base(databaseHelpersService, databaseConnection, logger, httpContextAccessor)
    {
        this.databaseConnection = databaseConnection;
        this.logger = logger;
        this.shoppingBasketsService = shoppingBasketsService;
        this.gclSettings = gclSettings.Value;
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public async Task<PaymentRequestResult> HandlePaymentRequestAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> conceptOrders, WiserItemModel userDetails,
        PaymentMethodSettingsModel paymentMethodSettings, string invoiceNumber)
    {
        var payNlSettings = (PayNLSettingsModel)paymentMethodSettings.PaymentServiceProvider;
        var validationResult = ValidatePayNLSettings(payNlSettings);
        if (!validationResult.Valid)
        {
            logger.LogError("Validation in 'HandlePaymentRequestAsync' of 'PayNlService' failed because: {Message}", validationResult.Message);
            return new PaymentRequestResult
            {
                Successful = false,
                Action = PaymentRequestActions.Redirect,
                ActionData = payNlSettings.FailUrl
            };
        }

        var totalPrice = await CalculatePriceAsync(conceptOrders);

        // Build and execute payment request.
        var restClient = CreateRestClient(payNlSettings);
        var restRequest = CreateTransactionStartRequest(totalPrice, payNlSettings, invoiceNumber);
        var restResponse = await restClient.ExecuteAsync(restRequest);
        var responseJson = JObject.Parse(restResponse.Content);
        var responseSuccessful = restResponse.StatusCode == HttpStatusCode.Created;

        return new PaymentRequestResult
        {
            Successful = responseSuccessful,
            Action = PaymentRequestActions.Redirect,
            ActionData = (responseSuccessful) ? responseJson["paymentUrl"]?.ToString() : payNlSettings.FailUrl
        };
    }

    /// <inheritdoc />
    public async Task<StatusUpdateResult> ProcessStatusUpdateAsync(OrderProcessSettingsModel orderProcessSettings,
        PaymentMethodSettingsModel paymentMethodSettings)
    {
        if (httpContextAccessor?.HttpContext == null)
        {
            return new StatusUpdateResult
            {
                Successful = false,
                Status = "Error retrieving status: No HttpContext available."
            };
        }
        // The settings have been checked during transaction creation so we don't do so again
        var payNlSettings = (PayNLSettingsModel)paymentMethodSettings.PaymentServiceProvider;

        var restClient = CreateRestClient(payNlSettings);
        var payNlTransactionId = httpContextAccessor.HttpContext.Request.Form["id"];
        var restRequest = new RestRequest($"/v2/transactions/{payNlTransactionId}");
        var restResponse = await restClient.ExecuteAsync(restRequest);

        if (restResponse.StatusCode != HttpStatusCode.OK)
        {
            return new StatusUpdateResult
            {
                Successful = false,
                Status = "error"
            };
        }
        var responseJson = JObject.Parse(restResponse.Content);
        var status = responseJson["status"]?["action"]?.ToString();

        if (String.IsNullOrWhiteSpace(status))
        {
            await LogIncomingPaymentActionAsync(PaymentServiceProviders.PayNl, String.Empty, (int)restResponse.StatusCode, responseBody: restResponse.Content);
            return new StatusUpdateResult
            {
                Successful = false,
                Status = "error"
            };
        }

        var invoiceNumber = responseJson["orderId"]?.ToString();

        await LogIncomingPaymentActionAsync(PaymentServiceProviders.PayNl, invoiceNumber, (int)restResponse.StatusCode, responseBody: restResponse.Content);

        return new StatusUpdateResult
        {
            Successful = status.Equals("paid", StringComparison.OrdinalIgnoreCase),
            Status = status
        };
    }

    /// <inheritdoc />
    public async Task<PaymentServiceProviderSettingsModel> GetProviderSettingsAsync(PaymentServiceProviderSettingsModel paymentServiceProviderSettings)
    {
        databaseConnection.AddParameter("id", paymentServiceProviderSettings.Id);

        var query = $@"SELECT
    payNlUsernameLive.`value` AS payNlUsernameLive,
    payNlUsernameTest.`value` AS payNlUsernameTest,
    payNlPasswordLive.`value` AS payNlPasswordLive,
    payNlPasswordTest.`value` AS payNlPasswordTest,
    payNlServiceIdLive.`value` AS payNlServiceIdLive,
    payNlServiceIdTest.`value` AS payNlServiceIdTest
FROM {WiserTableNames.WiserItem} AS paymentServiceProvider
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlUsernameLive ON payNlUsernameLive.item_id = paymentServiceProvider.id AND payNlUsernameLive.`key` = '{PayNlConstants.PayNlUsernameLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlUsernameTest ON payNlUsernameTest.item_id = paymentServiceProvider.id AND payNlUsernameTest.`key` = '{PayNlConstants.PayNlUsernameTestProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlPasswordLive ON payNlPasswordLive.item_id = paymentServiceProvider.id AND payNlPasswordLive.`key` = '{PayNlConstants.PayNlPasswordLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlPasswordTest ON payNlPasswordTest.item_id = paymentServiceProvider.id AND payNlPasswordTest.`key` = '{PayNlConstants.PayNlPasswordTestProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlServiceIdLive ON payNlPasswordTest.item_id = paymentServiceProvider.id AND payNlServiceIdLive.`key` = '{PayNlConstants.PayNlServiceIdLiveProperty}'
LEFT JOIN {WiserTableNames.WiserItemDetail} AS payNlServiceIdTest ON payNlServiceIdTest.item_id = paymentServiceProvider.id AND payNlServiceIdTest.`key` = '{PayNlConstants.PayNlServiceIdTestProperty}'
WHERE paymentServiceProvider.id = ?id
AND paymentServiceProvider.entity_type = '{Constants.PaymentServiceProviderEntityType}'";


        var result = new PayNLSettingsModel
        {
            Id = paymentServiceProviderSettings.Id,
            Title = paymentServiceProviderSettings.Title,
            Type = paymentServiceProviderSettings.Type,
            LogAllRequests = paymentServiceProviderSettings.LogAllRequests,
            OrdersCanBeSetDirectlyToFinished = paymentServiceProviderSettings.OrdersCanBeSetDirectlyToFinished,
            SkipPaymentWhenOrderAmountEqualsZero = paymentServiceProviderSettings.SkipPaymentWhenOrderAmountEqualsZero
        };

        var dataTable = await databaseConnection.GetAsync(query);
        if (dataTable.Rows.Count == 0)
        {
            return result;
        }

        var row = dataTable.Rows[0];

        var suffix = gclSettings.Environment.InList(Environments.Development, Environments.Test) ? "Test" : "Live";
        result.Username = row.GetAndDecryptSecretKey($"payNlUsername{suffix}");
        result.Password = row.GetAndDecryptSecretKey($"payNlPassword{suffix}");
        result.ServiceId = row.GetAndDecryptSecretKey($"payNlServiceId{suffix}");
        return result;
    }

    /// <inheritdoc />
    public string GetInvoiceNumberFromRequest()
    {
        return HttpContextHelpers.GetRequestValue(httpContextAccessor?.HttpContext, PayNlConstants.WebhookInvoiceNumberProperty);
    }

    private static RestClient CreateRestClient(PayNLSettingsModel payNlSettings)
    {
        return new RestClient(new RestClientOptions(BaseUrl)
        {
            Authenticator = new HttpBasicAuthenticator(payNlSettings.Username, payNlSettings.Password)
        });
    }

    private async Task<decimal> CalculatePriceAsync(ICollection<(WiserItemModel Main, List<WiserItemModel> Lines)> conceptOrders)
    {
        var basketSettings = await shoppingBasketsService.GetSettingsAsync();

        var totalPrice = 0M;
        foreach (var (main, lines) in conceptOrders)
        {
            totalPrice += await shoppingBasketsService.GetPriceAsync(main, lines, basketSettings, ShoppingBasket.PriceTypes.PspPriceInVat);
        }

        return totalPrice;
    }

    private (bool Valid, string Message) ValidatePayNLSettings(PayNLSettingsModel payNlSettings)
    {
        if (String.IsNullOrEmpty(payNlSettings.Username) || String.IsNullOrEmpty(payNlSettings.Password))
        {
            return (false, "PayNL misconfigured: No username or password set.");
        }

        if (payNlSettings.Username.StartsWith("AT-") && String.IsNullOrEmpty(payNlSettings.ServiceId))
        {
            return (false, "PayNL misconfigured: Username is an AT-code but no ServiceId is set.");
        }

        return (true, null);
    }

    private RestRequest CreateTransactionStartRequest(decimal totalPrice, PayNLSettingsModel payNlSettings, string invoiceNumber)
    {
        var restRequest = new RestRequest("/v2/transactions", Method.Post);

        restRequest.AddJsonBody(new TransactionStartBody
        {
            ServiceId = payNlSettings.ServiceId,
            Amount = new Amount
            {
                Value = (int)Math.Round(totalPrice * 100),
                Currency = payNlSettings.Currency
            },
            Description = $"Order #{invoiceNumber}",
            ReturnUrl = payNlSettings.ReturnUrl,
            ExchangeUrl = payNlSettings.WebhookUrl,
            Integration = new Integration
            {
                TestMode = gclSettings.Environment.InList(Environments.Test, Environments.Development)
            }
        });

        return restRequest;
    }
}