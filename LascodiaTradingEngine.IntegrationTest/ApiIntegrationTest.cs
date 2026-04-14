using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LascodiaTradingEngine.Application.Common.Events;
using LascodiaTradingEngine.Application.StrategyGeneration;
using LascodiaTradingEngine.Domain.Entities;
using LascodiaTradingEngine.Domain.Enums;
using LascodiaTradingEngine.Infrastructure.Persistence.DbContexts;
using LascodiaTradingEngine.IntegrationTest.Fixtures;

namespace LascodiaTradingEngine.IntegrationTest;

public class ApiIntegrationTest : IClassFixture<PostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly PostgresFixture _fixture;

    public ApiIntegrationTest(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Strategy_Endpoint_RequiresAuthentication()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/lascodia-trading-engine/strategy/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Strategy_Create_Get_And_List_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/strategy",
            new
            {
                name = "API Strategy",
                description = "Created through the HTTP integration suite",
                strategyType = "MovingAverageCrossover",
                symbol = "EURUSD",
                timeframe = "H1",
                parametersJson = "{\"FastPeriod\":9,\"SlowPeriod\":21}"
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);
        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var createdId = createPayload.data;

        var getResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/strategy/{createdId}");
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await ReadAsAsync<ResponseEnvelope<StrategyApiDto>>(getResponse);

        Assert.True(getPayload.status);
        Assert.Equal("API Strategy", getPayload.data!.Name);
        Assert.Equal("EURUSD", getPayload.data.Symbol);
        Assert.Equal("Paused", getPayload.data.Status);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/strategy/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    search = "API Strategy"
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<StrategyApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        Assert.Contains(listPayload.data!.data, strategy => strategy.Id == createdId);
    }

    [Fact]
    public async Task Strategy_List_Filters_By_Screening_Metadata_Through_Http()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        await SeedStrategiesForFilterTestAsync(factory.Services);

        using var client = factory.CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/strategy/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    hasScreeningMetadata = true,
                    generationSource = "Reserve",
                    observedRegime = "Trending",
                    reserveTargetRegime = "Ranging",
                    autoPromotedOnly = true
                }
            });

        response.EnsureSuccessStatusCode();
        var payload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<StrategyApiDto>>>(response);

        Assert.True(payload.status);
        var strategy = Assert.Single(payload.data!.data);
        Assert.Equal("Auto-Reserve-RSIReversion-EURUSD-H1", strategy.Name);
        Assert.NotNull(strategy.ScreeningMetadata);
        Assert.Equal("Reserve", strategy.ScreeningMetadata!.GenerationSource);
        Assert.True(strategy.ScreeningMetadata.IsAutoPromoted);
    }

    [Fact]
    public async Task CurrencyPair_Create_Update_List_And_Delete_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/currency-pair",
            new
            {
                symbol = "eurusd",
                baseCurrency = "eur",
                quoteCurrency = "usd",
                decimalPlaces = 5,
                contractSize = 100000m,
                minLotSize = 0.01m,
                maxLotSize = 100m,
                lotStep = 0.01m
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);
        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var currencyPairId = createPayload.data;

        var getResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/currency-pair/{currencyPairId}");
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await ReadAsAsync<ResponseEnvelope<CurrencyPairApiDto>>(getResponse);

        Assert.True(getPayload.status);
        Assert.Equal("EURUSD", getPayload.data!.Symbol);
        Assert.Equal("EUR", getPayload.data.BaseCurrency);
        Assert.Equal("USD", getPayload.data.QuoteCurrency);
        Assert.True(getPayload.data.IsActive);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/currency-pair/{currencyPairId}",
            new
            {
                symbol = "gbpusd",
                baseCurrency = "gbp",
                quoteCurrency = "usd",
                decimalPlaces = 3,
                contractSize = 100000m,
                minLotSize = 0.10m,
                maxLotSize = 50m,
                lotStep = 0.10m,
                isActive = false
            });

        updateResponse.EnsureSuccessStatusCode();
        var updatePayload = await ReadAsAsync<ResponseEnvelope<string>>(updateResponse);
        Assert.True(updatePayload.status);

        var updatedResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/currency-pair/{currencyPairId}");
        updatedResponse.EnsureSuccessStatusCode();
        var updatedPayload = await ReadAsAsync<ResponseEnvelope<CurrencyPairApiDto>>(updatedResponse);

        Assert.True(updatedPayload.status);
        Assert.Equal("GBPUSD", updatedPayload.data!.Symbol);
        Assert.Equal(3, updatedPayload.data.DecimalPlaces);
        Assert.Equal(0.10m, updatedPayload.data.MinLotSize);
        Assert.False(updatedPayload.data.IsActive);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/currency-pair/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    search = "GBPUSD",
                    isActive = false
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<CurrencyPairApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var listedCurrencyPair = Assert.Single(listPayload.data!.data);
        Assert.Equal(currencyPairId, listedCurrencyPair.Id);
        Assert.Equal("GBPUSD", listedCurrencyPair.Symbol);

        var deleteResponse = await client.DeleteAsync($"/api/v1/lascodia-trading-engine/currency-pair/{currencyPairId}");
        deleteResponse.EnsureSuccessStatusCode();
        var deletePayload = await ReadAsAsync<ResponseEnvelope<string>>(deleteResponse);

        Assert.True(deletePayload.status);

        var deletedResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/currency-pair/{currencyPairId}");
        deletedResponse.EnsureSuccessStatusCode();
        var deletedPayload = await ReadAsAsync<ResponseEnvelope<CurrencyPairApiDto>>(deletedResponse);

        Assert.False(deletedPayload.status);
        Assert.Equal("Currency pair not found", deletedPayload.message);
    }

    [Fact]
    public async Task RiskProfile_DefaultSwitch_Audit_And_Delete_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var baselineCreateResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/risk-profile",
            new
            {
                name = "Baseline Conservative",
                maxLotSizePerTrade = 1.0m,
                maxDailyDrawdownPct = 2.0m,
                maxTotalDrawdownPct = 8.0m,
                maxOpenPositions = 3,
                maxDailyTrades = 8,
                maxRiskPerTradePct = 0.75m,
                maxSymbolExposurePct = 4.0m,
                isDefault = true,
                drawdownRecoveryThresholdPct = 1.0m,
                recoveryLotSizeMultiplier = 0.5m,
                recoveryExitThresholdPct = 0.4m
            });

        baselineCreateResponse.EnsureSuccessStatusCode();
        var baselinePayload = await ReadAsAsync<ResponseEnvelope<long>>(baselineCreateResponse);
        Assert.True(baselinePayload.status);

        var candidateCreateResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/risk-profile",
            new
            {
                name = "Swing Growth",
                maxLotSizePerTrade = 2.0m,
                maxDailyDrawdownPct = 3.0m,
                maxTotalDrawdownPct = 10.0m,
                maxOpenPositions = 5,
                maxDailyTrades = 15,
                maxRiskPerTradePct = 1.0m,
                maxSymbolExposurePct = 6.5m,
                isDefault = false,
                drawdownRecoveryThresholdPct = 1.5m,
                recoveryLotSizeMultiplier = 0.65m,
                recoveryExitThresholdPct = 0.55m
            });

        candidateCreateResponse.EnsureSuccessStatusCode();
        var candidatePayload = await ReadAsAsync<ResponseEnvelope<long>>(candidateCreateResponse);
        Assert.True(candidatePayload.status);

        var baselineId = baselinePayload.data;
        var candidateId = candidatePayload.data;

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/risk-profile/{candidateId}",
            new
            {
                name = "Swing Growth Updated",
                maxLotSizePerTrade = 2.5m,
                maxDailyDrawdownPct = 3.2m,
                maxTotalDrawdownPct = 11.0m,
                maxOpenPositions = 6,
                maxDailyTrades = 18,
                maxRiskPerTradePct = 1.2m,
                maxSymbolExposurePct = 7.0m,
                isDefault = true,
                drawdownRecoveryThresholdPct = 1.7m,
                recoveryLotSizeMultiplier = 0.70m,
                recoveryExitThresholdPct = 0.60m
            });

        updateResponse.EnsureSuccessStatusCode();
        var updatePayload = await ReadAsAsync<ResponseEnvelope<string>>(updateResponse);
        Assert.True(updatePayload.status);

        var updatedResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/risk-profile/{candidateId}");
        updatedResponse.EnsureSuccessStatusCode();
        var updatedPayload = await ReadAsAsync<ResponseEnvelope<RiskProfileApiDto>>(updatedResponse);

        Assert.True(updatedPayload.status);
        Assert.Equal("Swing Growth Updated", updatedPayload.data!.Name);
        Assert.Equal(2.5m, updatedPayload.data.MaxLotSizePerTrade);
        Assert.True(updatedPayload.data.IsDefault);

        var baselineGetResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/risk-profile/{baselineId}");
        baselineGetResponse.EnsureSuccessStatusCode();
        var baselineGetPayload = await ReadAsAsync<ResponseEnvelope<RiskProfileApiDto>>(baselineGetResponse);

        Assert.True(baselineGetPayload.status);
        Assert.False(baselineGetPayload.data!.IsDefault);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/risk-profile/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    search = "Swing Growth Updated"
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<RiskProfileApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var listedRiskProfile = Assert.Single(listPayload.data!.data);
        Assert.Equal(candidateId, listedRiskProfile.Id);
        Assert.True(listedRiskProfile.IsDefault);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WriteApplicationDbContext>();
            var auditLog = await context.Set<DecisionLog>()
                .OrderByDescending(entry => entry.CreatedAt)
                .FirstOrDefaultAsync(entry =>
                    entry.EntityType == "RiskProfile"
                    && entry.EntityId == candidateId
                    && entry.DecisionType == "RiskProfileUpdated");

            Assert.NotNull(auditLog);
            Assert.Equal("Updated", auditLog!.Outcome);
            Assert.Contains("Swing Growth Updated", auditLog.ContextJson ?? string.Empty);
        }

        var currentDefaultDeleteResponse = await client.DeleteAsync($"/api/v1/lascodia-trading-engine/risk-profile/{candidateId}");
        currentDefaultDeleteResponse.EnsureSuccessStatusCode();
        var currentDefaultDeletePayload = await ReadAsAsync<ResponseEnvelope<string>>(currentDefaultDeleteResponse);

        Assert.False(currentDefaultDeletePayload.status);
        Assert.Equal("Cannot delete the default risk profile", currentDefaultDeletePayload.message);

        var baselineDeleteResponse = await client.DeleteAsync($"/api/v1/lascodia-trading-engine/risk-profile/{baselineId}");
        baselineDeleteResponse.EnsureSuccessStatusCode();
        var baselineDeletePayload = await ReadAsAsync<ResponseEnvelope<string>>(baselineDeleteResponse);

        Assert.True(baselineDeletePayload.status);

        var deletedBaselineResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/risk-profile/{baselineId}");
        deletedBaselineResponse.EnsureSuccessStatusCode();
        var deletedBaselinePayload = await ReadAsAsync<ResponseEnvelope<RiskProfileApiDto>>(deletedBaselineResponse);

        Assert.False(deletedBaselinePayload.status);
        Assert.Equal("Risk profile not found", deletedBaselinePayload.message);
    }

    [Fact]
    public async Task TradingAccount_Create_Update_Activate_Sync_Rotate_And_Delete_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var firstAccountId = "HTTP-ACC-" + Guid.NewGuid().ToString("N")[..8];
        var secondAccountId = "HTTP-ACC-" + Guid.NewGuid().ToString("N")[..8];

        var createFirstResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/trading-account",
            new
            {
                accountId = firstAccountId,
                brokerServer = "demo-server",
                brokerName = "Broker Alpha",
                accountName = "Desk One",
                currency = "USD",
                accountType = "Demo",
                leverage = 100m,
                marginMode = "Hedging",
                isPaper = false
            });

        createFirstResponse.EnsureSuccessStatusCode();
        var createFirstPayload = await ReadAsAsync<ResponseEnvelope<long>>(createFirstResponse);
        Assert.True(createFirstPayload.status);

        var createSecondResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/trading-account",
            new
            {
                accountId = secondAccountId,
                brokerServer = "demo-server",
                brokerName = "Broker Alpha",
                accountName = "Desk Two",
                currency = "USD",
                accountType = "Real",
                leverage = 200m,
                marginMode = "Netting",
                isPaper = false
            });

        createSecondResponse.EnsureSuccessStatusCode();
        var createSecondPayload = await ReadAsAsync<ResponseEnvelope<long>>(createSecondResponse);
        Assert.True(createSecondPayload.status);

        var firstTradingAccountId = createFirstPayload.data;
        var secondTradingAccountId = createSecondPayload.data;

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}",
            new
            {
                accountName = "Desk One Prime",
                currency = "EUR",
                isPaper = true
            });

        updateResponse.EnsureSuccessStatusCode();
        var updatePayload = await ReadAsAsync<ResponseEnvelope<string>>(updateResponse);
        Assert.True(updatePayload.status);

        var syncResponse = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}/sync",
            new
            {
                balance = 12345m,
                equity = 12222m,
                marginUsed = 450m,
                marginAvailable = 11772m,
                leverage = 150m,
                marginMode = "Netting",
                marginLevel = 2716m,
                profit = 222m,
                credit = 10m,
                marginSoMode = "Percent",
                marginSoCall = 80m,
                marginSoStopOut = 50m
            });

        syncResponse.EnsureSuccessStatusCode();
        var syncPayload = await ReadAsAsync<ResponseEnvelope<string>>(syncResponse);
        Assert.True(syncPayload.status);
        Assert.Equal("Synced", syncPayload.data);

        var getResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}");
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await ReadAsAsync<ResponseEnvelope<TradingAccountApiDto>>(getResponse);

        Assert.True(getPayload.status);
        Assert.Equal("Desk One Prime", getPayload.data!.AccountName);
        Assert.Equal("EUR", getPayload.data.Currency);
        Assert.True(getPayload.data.IsPaper);
        Assert.Equal(12345m, getPayload.data.Balance);
        Assert.Equal(12222m, getPayload.data.Equity);
        Assert.Equal(450m, getPayload.data.MarginUsed);
        Assert.Equal("Netting", getPayload.data.MarginMode);
        Assert.True(getPayload.data.LastSyncedAt > DateTime.UtcNow.AddMinutes(-5));

        var rotateResponse = await client.PostAsync(
            $"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}/rotate-api-key",
            content: null);

        rotateResponse.EnsureSuccessStatusCode();
        var rotatePayload = await ReadAsAsync<ResponseEnvelope<RotateApiKeyApiDto>>(rotateResponse);

        Assert.True(rotatePayload.status);
        Assert.NotNull(rotatePayload.data);
        Assert.Equal(64, rotatePayload.data!.ApiKey!.Length);
        Assert.StartsWith("enc2:", rotatePayload.data.EncryptedApiKeyBlob);

        var activateFirstResponse = await client.PutAsync(
            $"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}/activate",
            content: null);

        activateFirstResponse.EnsureSuccessStatusCode();
        var activateFirstPayload = await ReadAsAsync<ResponseEnvelope<string>>(activateFirstResponse);
        Assert.True(activateFirstPayload.status);
        Assert.Equal("Activated", activateFirstPayload.data);

        var activeFirstResponse = await client.GetAsync("/api/v1/lascodia-trading-engine/trading-account/active");
        activeFirstResponse.EnsureSuccessStatusCode();
        var activeFirstPayload = await ReadAsAsync<ResponseEnvelope<TradingAccountApiDto>>(activeFirstResponse);

        Assert.True(activeFirstPayload.status);
        Assert.Equal(firstTradingAccountId, activeFirstPayload.data!.Id);

        var deleteActiveResponse = await client.DeleteAsync($"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}");
        deleteActiveResponse.EnsureSuccessStatusCode();
        var deleteActivePayload = await ReadAsAsync<ResponseEnvelope<string>>(deleteActiveResponse);

        Assert.False(deleteActivePayload.status);
        Assert.Equal("Cannot delete the active trading account", deleteActivePayload.message);

        var activateSecondResponse = await client.PutAsync(
            $"/api/v1/lascodia-trading-engine/trading-account/{secondTradingAccountId}/activate",
            content: null);

        activateSecondResponse.EnsureSuccessStatusCode();
        var activateSecondPayload = await ReadAsAsync<ResponseEnvelope<string>>(activateSecondResponse);
        Assert.True(activateSecondPayload.status);

        var activeSecondResponse = await client.GetAsync("/api/v1/lascodia-trading-engine/trading-account/active");
        activeSecondResponse.EnsureSuccessStatusCode();
        var activeSecondPayload = await ReadAsAsync<ResponseEnvelope<TradingAccountApiDto>>(activeSecondResponse);

        Assert.True(activeSecondPayload.status);
        Assert.Equal(secondTradingAccountId, activeSecondPayload.data!.Id);

        var deleteInactiveResponse = await client.DeleteAsync($"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}");
        deleteInactiveResponse.EnsureSuccessStatusCode();
        var deleteInactivePayload = await ReadAsAsync<ResponseEnvelope<string>>(deleteInactiveResponse);

        Assert.True(deleteInactivePayload.status);
        Assert.Equal("Deleted", deleteInactivePayload.data);

        var deletedResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/trading-account/{firstTradingAccountId}");
        deletedResponse.EnsureSuccessStatusCode();
        var deletedPayload = await ReadAsAsync<ResponseEnvelope<TradingAccountApiDto>>(deletedResponse);

        Assert.False(deletedPayload.status);
        Assert.Equal("Trading account not found", deletedPayload.message);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/trading-account/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    brokerName = "Broker Alpha",
                    isPaper = false
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<TradingAccountApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var listedTradingAccount = Assert.Single(listPayload.data!.data);
        Assert.Equal(secondTradingAccountId, listedTradingAccount.Id);
        Assert.Equal("Desk Two", listedTradingAccount.AccountName);
    }

    [Fact]
    public async Task TradingAccount_Register_And_Login_Supports_Web_And_Ea_Flows()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var accountId = "AUTH-ACC-" + Guid.NewGuid().ToString("N")[..8];
        const string brokerServer = "auth-demo-server";
        const string password = "Stronger!123";

        var registerResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/auth/register",
            new
            {
                accountId,
                brokerServer,
                brokerName = "Broker Auth",
                accountName = "Authentication Desk",
                password,
                currency = "USD",
                accountType = "Demo",
                leverage = 100m,
                marginMode = "Hedging",
                isPaper = true
            });

        registerResponse.EnsureSuccessStatusCode();
        var registerPayload = await ReadAsAsync<ResponseEnvelope<AuthTokenApiDto>>(registerResponse);

        Assert.True(registerPayload.status);
        Assert.NotNull(registerPayload.data);
        Assert.Equal("Bearer", registerPayload.data!.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(registerPayload.data.Token));
        Assert.Equal(accountId, registerPayload.data.Account!.AccountId);
        Assert.Equal(64, registerPayload.data.ApiKey!.Length);
        Assert.StartsWith("enc2:", registerPayload.data.EncryptedApiKeyBlob);

        var repeatRegisterResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/auth/register",
            new
            {
                accountId,
                brokerServer,
                brokerName = "Broker Auth",
                accountName = "Authentication Desk",
                password,
                currency = "USD",
                accountType = "Demo",
                leverage = 100m,
                marginMode = "Hedging",
                isPaper = true
            });

        repeatRegisterResponse.EnsureSuccessStatusCode();
        var repeatRegisterPayload = await ReadAsAsync<ResponseEnvelope<AuthTokenApiDto>>(repeatRegisterResponse);

        Assert.True(repeatRegisterPayload.status);
        Assert.Equal(registerPayload.data.Account.Id, repeatRegisterPayload.data!.Account!.Id);
        Assert.Equal(registerPayload.data.ApiKey, repeatRegisterPayload.data.ApiKey);

        var webLoginResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/auth/login",
            new
            {
                accountId,
                brokerServer,
                password,
                loginSource = "web"
            });

        webLoginResponse.EnsureSuccessStatusCode();
        var webLoginPayload = await ReadAsAsync<ResponseEnvelope<AuthTokenApiDto>>(webLoginResponse);

        Assert.True(webLoginPayload.status);
        Assert.NotNull(webLoginPayload.data);
        Assert.False(string.IsNullOrWhiteSpace(webLoginPayload.data!.Token));
        Assert.Null(webLoginPayload.data.ApiKey);

        var invalidWebLoginResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/auth/login",
            new
            {
                accountId,
                brokerServer,
                password = "Wrong!123",
                loginSource = "web"
            });

        invalidWebLoginResponse.EnsureSuccessStatusCode();
        var invalidWebLoginPayload = await ReadAsAsync<ResponseEnvelope<AuthTokenApiDto>>(invalidWebLoginResponse);

        Assert.False(invalidWebLoginPayload.status);
        Assert.Equal("Invalid credentials", invalidWebLoginPayload.message);

        var eaLoginResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/auth/login",
            new
            {
                accountId,
                brokerServer,
                encryptedApiKeyBlob = registerPayload.data.EncryptedApiKeyBlob,
                loginSource = "ea"
            });

        eaLoginResponse.EnsureSuccessStatusCode();
        var eaLoginPayload = await ReadAsAsync<ResponseEnvelope<AuthTokenApiDto>>(eaLoginResponse);

        Assert.True(eaLoginPayload.status);
        Assert.NotNull(eaLoginPayload.data);
        Assert.False(string.IsNullOrWhiteSpace(eaLoginPayload.data!.Token));
        Assert.Equal(registerPayload.data.Account.Id, eaLoginPayload.data.Account!.Id);
    }

    [Fact]
    public async Task AuditTrail_Log_And_List_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/audit-trail",
            new
            {
                entityType = "Strategy",
                entityId = 42,
                decisionType = "Promoted",
                outcome = "Approved",
                reason = "Sharpe and profit factor exceeded thresholds",
                contextJson = "{\"before\":\"candidate\",\"after\":\"active\"}",
                source = "ApiIntegrationTest"
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);

        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/audit-trail/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    entityType = "Strategy",
                    entityId = 42,
                    decisionType = "Promoted",
                    outcome = "Approved"
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<DecisionLogApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var auditEntry = Assert.Single(listPayload.data!.data);
        Assert.Equal(createPayload.data, auditEntry.Id);
        Assert.Equal("Strategy", auditEntry.EntityType);
        Assert.Equal("Promoted", auditEntry.DecisionType);
        Assert.Equal("Approved", auditEntry.Outcome);
    }

    [Fact]
    public async Task ExecutionQuality_Record_Get_And_List_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();
        var (strategyId, tradingAccountId) = await SeedOrderPrerequisitesAsync(factory.Services);

        var createOrderResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/order",
            new
            {
                strategyId,
                tradingAccountId,
                symbol = "EURUSD",
                orderType = "Buy",
                executionType = "Market",
                quantity = 0.15m,
                price = 0m,
                stopLoss = 1.0950m,
                takeProfit = 1.1100m,
                isPaper = true,
                notes = "Execution quality seed order"
            });

        createOrderResponse.EnsureSuccessStatusCode();
        var createOrderPayload = await ReadAsAsync<ResponseEnvelope<long>>(createOrderResponse);

        Assert.True(createOrderPayload.status);
        Assert.True(createOrderPayload.data > 0);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/execution-quality",
            new
            {
                orderId = createOrderPayload.data,
                strategyId,
                symbol = "EURUSD",
                session = "London",
                requestedPrice = 1.1000m,
                filledPrice = 1.1003m,
                slippagePips = 3.0m,
                submitToFillMs = 245L,
                wasPartialFill = false,
                fillRate = 1.0m
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);

        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var getResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/execution-quality/{createPayload.data}");
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await ReadAsAsync<ResponseEnvelope<ExecutionQualityLogApiDto>>(getResponse);

        Assert.True(getPayload.status);
        Assert.Equal(createOrderPayload.data, getPayload.data!.OrderId);
        Assert.Equal("EURUSD", getPayload.data.Symbol);
        Assert.Equal("London", getPayload.data.Session);
        Assert.Equal(3.0m, getPayload.data.SlippagePips);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/execution-quality/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    symbol = "EURUSD",
                    session = "London",
                    strategyId
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<ExecutionQualityLogApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var executionLog = Assert.Single(listPayload.data!.data);
        Assert.Equal(createPayload.data, executionLog.Id);
        Assert.Equal(strategyId, executionLog.StrategyId);
    }

    [Fact]
    public async Task EconomicEvent_Create_Update_And_List_RoundTrip_Succeeds()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        var scheduledAt = DateTime.UtcNow.AddDays(1);

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/economic-event",
            new
            {
                title = "US CPI",
                currency = "usd",
                impact = "High",
                scheduledAt,
                source = "Manual",
                forecast = "3.4%",
                previous = "3.2%",
                externalKey = "cpi-us-" + Guid.NewGuid().ToString("N")[..8]
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);

        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var updateResponse = await client.PutAsJsonAsync(
            $"/api/v1/lascodia-trading-engine/economic-event/{createPayload.data}/actual",
            new
            {
                actual = "3.5%"
            });

        updateResponse.EnsureSuccessStatusCode();
        var updatePayload = await ReadAsAsync<ResponseEnvelope<string>>(updateResponse);

        Assert.True(updatePayload.status);
        Assert.Equal("Actual value updated successfully", updatePayload.data);

        var listResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/economic-event/list",
            new
            {
                currentPage = 1,
                itemCountPerPage = 10,
                filter = new
                {
                    currency = "USD",
                    impact = "High",
                    from = scheduledAt.AddHours(-1),
                    to = scheduledAt.AddHours(1)
                }
            });

        listResponse.EnsureSuccessStatusCode();
        var listPayload = await ReadAsAsync<ResponseEnvelope<PagedEnvelope<EconomicEventApiDto>>>(listResponse);

        Assert.True(listPayload.status);
        var economicEvent = Assert.Single(listPayload.data!.data);
        Assert.Equal(createPayload.data, economicEvent.Id);
        Assert.Equal("US CPI", economicEvent.Title);
        Assert.Equal("USD", economicEvent.Currency);
        Assert.Equal("High", economicEvent.Impact);
        Assert.Equal("3.5%", economicEvent.Actual);
    }

    [Fact]
    public async Task Order_Create_And_Get_Publishes_Event_And_Persists()
    {
        await ResetDatabaseAsync();
        using var factory = CreateFactory();
        var (strategyId, tradingAccountId) = await SeedOrderPrerequisitesAsync(factory.Services);

        using var client = factory.CreateAuthenticatedClient();
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/lascodia-trading-engine/order",
            new
            {
                strategyId,
                tradingAccountId,
                symbol = "EURUSD",
                orderType = "Buy",
                executionType = "Market",
                quantity = 0.25m,
                price = 0m,
                stopLoss = 1.0950m,
                takeProfit = 1.1100m,
                isPaper = true,
                notes = "HTTP integration test"
            });

        createResponse.EnsureSuccessStatusCode();
        var createPayload = await ReadAsAsync<ResponseEnvelope<long>>(createResponse);

        Assert.True(createPayload.status);
        Assert.True(createPayload.data > 0);

        var getResponse = await client.GetAsync($"/api/v1/lascodia-trading-engine/order/{createPayload.data}");
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await ReadAsAsync<ResponseEnvelope<OrderApiDto>>(getResponse);

        Assert.True(getPayload.status);
        Assert.Equal("EURUSD", getPayload.data!.Symbol);
        Assert.Equal("Pending", getPayload.data.Status);
        Assert.Equal("Buy", getPayload.data.OrderType);

        await using var scope = factory.Services.CreateAsyncScope();
        var eventLogDb = scope.ServiceProvider.GetRequiredService<EventLogDbContext>();
        var publishedEvent = await eventLogDb.IntegrationEventLogs
            .OrderByDescending(entry => entry.CreationTime)
            .FirstOrDefaultAsync(entry => entry.EventTypeName.EndsWith(nameof(OrderCreatedIntegrationEvent)));

        Assert.NotNull(publishedEvent);
        Assert.Contains(createPayload.data.ToString(), publishedEvent!.Content);
        Assert.Contains("\"Symbol\": \"EURUSD\"", publishedEvent.Content);
    }

    private ApiWebApplicationFactory CreateFactory() => new(_fixture.ConnectionString);

    private WriteApplicationDbContext CreateWriteContext()
    {
        var options = new DbContextOptionsBuilder<WriteApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        return new WriteApplicationDbContext(options, new HttpContextAccessor());
    }

    private async Task ResetDatabaseAsync()
    {
        await using var context = CreateWriteContext();
        await context.Database.EnsureDeletedAsync();
    }

    private static async Task<T> ReadAsAsync<T>(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
        return payload ?? throw new InvalidOperationException("Expected response payload.");
    }

    private static async Task SeedStrategiesForFilterTestAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WriteApplicationDbContext>();

        context.Set<Strategy>().AddRange(
            new Strategy
            {
                Name = "Manual-EURUSD-H1",
                Description = "Manual strategy",
                StrategyType = StrategyType.Custom,
                Symbol = "EURUSD",
                Timeframe = Timeframe.H1,
                ParametersJson = "{}",
                Status = StrategyStatus.Active
            },
            new Strategy
            {
                Name = "Auto-Reserve-RSIReversion-EURUSD-H1",
                Description = "Reserve auto",
                StrategyType = StrategyType.RSIReversion,
                Symbol = "EURUSD",
                Timeframe = Timeframe.M15,
                ParametersJson = "{\"Template\":\"Reserve\"}",
                Status = StrategyStatus.Paused,
                ScreeningMetricsJson = new ScreeningMetrics
                {
                    Regime = MarketRegime.Trending.ToString(),
                    ObservedRegime = MarketRegime.Trending.ToString(),
                    GenerationSource = "Reserve",
                    ReserveTargetRegime = MarketRegime.Ranging.ToString(),
                    IsAutoPromoted = true,
                    IsWinRate = 0.72,
                    IsProfitFactor = 1.9,
                    IsSharpeRatio = 1.5,
                    OosWinRate = 0.68,
                    OosProfitFactor = 1.7,
                    OosSharpeRatio = 1.2
                }.ToJson(),
            });

        await context.SaveChangesAsync();
    }

    private static async Task<(long StrategyId, long TradingAccountId)> SeedOrderPrerequisitesAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WriteApplicationDbContext>();

        var strategy = new Strategy
        {
            Name = "Order API Strategy",
            Description = "Strategy seed for API order integration test",
            StrategyType = StrategyType.MovingAverageCrossover,
            Symbol = "EURUSD",
            Timeframe = Timeframe.H1,
            ParametersJson = "{}",
            Status = StrategyStatus.Active
        };

        var account = new TradingAccount
        {
            AccountId = "API-ORDER-" + Guid.NewGuid().ToString("N")[..8],
            AccountName = "API Order Test Account",
            BrokerServer = "test-server",
            BrokerName = "TestBroker",
            AccountType = AccountType.Demo,
            Currency = "USD",
            Balance = 10000m,
            Equity = 10000m,
            MarginUsed = 0m,
            MarginAvailable = 10000m,
            IsActive = true,
            IsPaper = true
        };

        context.Set<Strategy>().Add(strategy);
        context.Set<TradingAccount>().Add(account);
        await context.SaveChangesAsync();

        return (strategy.Id, account.Id);
    }

    private sealed class ResponseEnvelope<T>
    {
        public T? data { get; set; }
        public bool status { get; set; }
        public string? message { get; set; }
        public string? responseCode { get; set; }
    }

    private sealed class PagedEnvelope<T>
    {
        public List<T> data { get; set; } = [];
        public PagerEnvelope? pager { get; set; }
    }

    private sealed class PagerEnvelope
    {
        public int TotalItemCount { get; set; }
        public int CurrentPage { get; set; }
        public int ItemCountPerPage { get; set; }
    }

    private sealed class StrategyApiDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Symbol { get; set; }
        public string? Status { get; set; }
        public StrategyScreeningMetadataApiDto? ScreeningMetadata { get; set; }
    }

    private sealed class StrategyScreeningMetadataApiDto
    {
        public string? GenerationSource { get; set; }
        public string? ObservedRegime { get; set; }
        public string? ReserveTargetRegime { get; set; }
        public bool IsAutoPromoted { get; set; }
    }

    private sealed class CurrencyPairApiDto
    {
        public long Id { get; set; }
        public string? Symbol { get; set; }
        public string? BaseCurrency { get; set; }
        public string? QuoteCurrency { get; set; }
        public int DecimalPlaces { get; set; }
        public decimal ContractSize { get; set; }
        public decimal MinLotSize { get; set; }
        public decimal MaxLotSize { get; set; }
        public decimal LotStep { get; set; }
        public bool IsActive { get; set; }
    }

    private sealed class RiskProfileApiDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public decimal MaxLotSizePerTrade { get; set; }
        public decimal MaxDailyDrawdownPct { get; set; }
        public decimal MaxTotalDrawdownPct { get; set; }
        public int MaxOpenPositions { get; set; }
        public int MaxDailyTrades { get; set; }
        public decimal MaxRiskPerTradePct { get; set; }
        public decimal MaxSymbolExposurePct { get; set; }
        public bool IsDefault { get; set; }
        public decimal DrawdownRecoveryThresholdPct { get; set; }
        public decimal RecoveryLotSizeMultiplier { get; set; }
        public decimal RecoveryExitThresholdPct { get; set; }
    }

    private sealed class TradingAccountApiDto
    {
        public long Id { get; set; }
        public string? AccountId { get; set; }
        public string? AccountName { get; set; }
        public string? BrokerServer { get; set; }
        public string? BrokerName { get; set; }
        public string? AccountType { get; set; }
        public decimal Leverage { get; set; }
        public string? MarginMode { get; set; }
        public string? Currency { get; set; }
        public decimal Balance { get; set; }
        public decimal Equity { get; set; }
        public decimal MarginUsed { get; set; }
        public decimal MarginAvailable { get; set; }
        public bool IsActive { get; set; }
        public bool IsPaper { get; set; }
        public DateTime LastSyncedAt { get; set; }
    }

    private sealed class RotateApiKeyApiDto
    {
        public string? ApiKey { get; set; }
        public string EncryptedApiKeyBlob { get; set; } = string.Empty;
    }

    private sealed class AuthTokenApiDto
    {
        public string Token { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public string TokenType { get; set; } = string.Empty;
        public AuthAccountSummaryApiDto? Account { get; set; }
        public string? ApiKey { get; set; }
        public string EncryptedApiKeyBlob { get; set; } = string.Empty;
    }

    private sealed class AuthAccountSummaryApiDto
    {
        public long Id { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string BrokerServer { get; set; } = string.Empty;
        public string BrokerName { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
    }

    private sealed class DecisionLogApiDto
    {
        public long Id { get; set; }
        public string? EntityType { get; set; }
        public long EntityId { get; set; }
        public string? DecisionType { get; set; }
        public string? Outcome { get; set; }
        public string? Reason { get; set; }
        public string? ContextJson { get; set; }
        public string? Source { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private sealed class ExecutionQualityLogApiDto
    {
        public long Id { get; set; }
        public long OrderId { get; set; }
        public long? StrategyId { get; set; }
        public string? Symbol { get; set; }
        public string? Session { get; set; }
        public decimal RequestedPrice { get; set; }
        public decimal FilledPrice { get; set; }
        public decimal SlippagePips { get; set; }
        public long SubmitToFillMs { get; set; }
        public bool WasPartialFill { get; set; }
        public decimal FillRate { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    private sealed class EconomicEventApiDto
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Currency { get; set; }
        public string? Impact { get; set; }
        public DateTime ScheduledAt { get; set; }
        public string? Forecast { get; set; }
        public string? Previous { get; set; }
        public string? Actual { get; set; }
        public string? Source { get; set; }
    }

    private sealed class OrderApiDto
    {
        public long Id { get; set; }
        public string? Symbol { get; set; }
        public string? Status { get; set; }
        public string? OrderType { get; set; }
    }
}
