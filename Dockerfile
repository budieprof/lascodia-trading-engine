# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and all project files first for layer caching
COPY LascodiaTradingEngine.slnx ./
COPY LascodiaTradingEngine.Domain/LascodiaTradingEngine.Domain.csproj LascodiaTradingEngine.Domain/
COPY LascodiaTradingEngine.Application/LascodiaTradingEngine.Application.csproj LascodiaTradingEngine.Application/
COPY LascodiaTradingEngine.Infrastructure/LascodiaTradingEngine.Infrastructure.csproj LascodiaTradingEngine.Infrastructure/
COPY LascodiaTradingEngine.API/LascodiaTradingEngine.API.csproj LascodiaTradingEngine.API/
COPY LascodiaTradingEngine.UnitTest/LascodiaTradingEngine.UnitTest.csproj LascodiaTradingEngine.UnitTest/
COPY LascodiaTradingEngine.IntegrationTest/LascodiaTradingEngine.IntegrationTest.csproj LascodiaTradingEngine.IntegrationTest/

# Shared library submodule project files
COPY submodules/shared/Library/SharedDomain/SharedDomain.csproj submodules/shared/Library/SharedDomain/
COPY submodules/shared/Library/SharedApplication/SharedApplication.csproj submodules/shared/Library/SharedApplication/
COPY submodules/shared/Library/SharedLibrary/SharedLibrary.csproj submodules/shared/Library/SharedLibrary/
COPY submodules/shared/Library/SharedInfrastructure/SharedInfrastructure.csproj submodules/shared/Library/SharedInfrastructure/
COPY submodules/shared/Library/SharedAPI/SharedAPI.csproj submodules/shared/Library/SharedAPI/
COPY submodules/shared/EventBus/EventBus/EventBus.csproj submodules/shared/EventBus/EventBus/
COPY submodules/shared/EventBus/EventBusRabbitMQ/EventBusRabbitMQ.csproj submodules/shared/EventBus/EventBusRabbitMQ/
COPY submodules/shared/EventBus/EventBusKafka/EventBusKafka.csproj submodules/shared/EventBus/EventBusKafka/
COPY submodules/shared/EventBus/IntegrationEventLogEF/IntegrationEventLogEF.csproj submodules/shared/EventBus/IntegrationEventLogEF/
COPY submodules/shared/lascodia-trading-engine-shared-library.slnx submodules/shared/

RUN dotnet restore

# Copy everything and build
COPY . .
# --runtime linux-<arch> --self-contained false flattens RID-specific native binaries
# (libtorch.so, libLibTorchSharp.so, etc.) from runtimes/<rid>/native/ into the
# publish root. Without this, TorchSharp's runtime loader searches only /app/ and
# fails to find libtorch, making every non-BaggedLogistic ML trainer (AdaBoost, TCN,
# GBM, TabNet, SVGP, FT-Transformer, ELM, Rocket, QuantileRF, DANN, SMOTE) fall over
# at init with "type initializer for 'TorchSharp.torch' threw an exception".
# TARGETARCH comes from buildx (amd64|arm64); mapped to the matching linux RID.
RUN RID="linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)" && \
    dotnet publish LascodiaTradingEngine.API/LascodiaTradingEngine.API.csproj \
    --configuration Release \
    --runtime "$RID" \
    --self-contained false \
    --output /app/publish

# ── Stage 2: Runtime ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Install curl for health checks and create non-root user
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* && \
    if ! getent group 1000 >/dev/null; then groupadd --gid 1000 appuser; fi && \
    if ! getent passwd 1000 >/dev/null; then useradd --uid 1000 --gid 1000 --shell /bin/false --create-home appuser; fi

COPY --from=build /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

USER 1000
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "LascodiaTradingEngine.API.dll"]
