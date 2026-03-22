using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LascodiaTradingEngine.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EfMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MLA2cLogs");

            migrationBuilder.DropTable(
                name: "MLAbsorptionRatioLogs");

            migrationBuilder.DropTable(
                name: "MLAcdDurationLogs");

            migrationBuilder.DropTable(
                name: "MLActComplexityLogs");

            migrationBuilder.DropTable(
                name: "MLActivationMaxLog");

            migrationBuilder.DropTable(
                name: "MLAdaptiveConformalLogs");

            migrationBuilder.DropTable(
                name: "MLAdaptiveDropoutLogs");

            migrationBuilder.DropTable(
                name: "MLAdaptiveWindowConfig");

            migrationBuilder.DropTable(
                name: "MLAdversarialRobustnessLog");

            migrationBuilder.DropTable(
                name: "MLAdversarialValidationLog");

            migrationBuilder.DropTable(
                name: "MLAdwinLogs");

            migrationBuilder.DropTable(
                name: "MLAleLogs");

            migrationBuilder.DropTable(
                name: "MLAlmgrenChrissLogs");

            migrationBuilder.DropTable(
                name: "MLAlphaStableLogs");

            migrationBuilder.DropTable(
                name: "MLAmihudLogs");

            migrationBuilder.DropTable(
                name: "MLArchLmTestLogs");

            migrationBuilder.DropTable(
                name: "MLAttentionWeightLog");

            migrationBuilder.DropTable(
                name: "MLBaldLogs");

            migrationBuilder.DropTable(
                name: "MLBarlowTwinsLogs");

            migrationBuilder.DropTable(
                name: "MLBatesCalibLogs");

            migrationBuilder.DropTable(
                name: "MLBayesFactorLogs");

            migrationBuilder.DropTable(
                name: "MLBayesThresholdLog");

            migrationBuilder.DropTable(
                name: "MLBetaVaeLogs");

            migrationBuilder.DropTable(
                name: "MLBipowerVariationLogs");

            migrationBuilder.DropTable(
                name: "MLBivariateEvtLogs");

            migrationBuilder.DropTable(
                name: "MLBlackLittermanLogs");

            migrationBuilder.DropTable(
                name: "MLBocpdChangePoint");

            migrationBuilder.DropTable(
                name: "MLBocpdLogs");

            migrationBuilder.DropTable(
                name: "MLBootstrapEnsemble");

            migrationBuilder.DropTable(
                name: "MLBvarLogs");

            migrationBuilder.DropTable(
                name: "MLC51Distribution");

            migrationBuilder.DropTable(
                name: "MLCandleBertEncoder");

            migrationBuilder.DropTable(
                name: "MLCandlestickLogs");

            migrationBuilder.DropTable(
                name: "MLCarrLogs");

            migrationBuilder.DropTable(
                name: "MLCarryDecompLogs");

            migrationBuilder.DropTable(
                name: "MLCashSelectionLog");

            migrationBuilder.DropTable(
                name: "MLCausalImpactLogs");

            migrationBuilder.DropTable(
                name: "MLCipLogs");

            migrationBuilder.DropTable(
                name: "MLClarkWestLogs");

            migrationBuilder.DropTable(
                name: "MLCmimFeatureRankingLog");

            migrationBuilder.DropTable(
                name: "MLCojumpLogs");

            migrationBuilder.DropTable(
                name: "MLConformalAnomalyLog");

            migrationBuilder.DropTable(
                name: "MLConformalEfficiencyLog");

            migrationBuilder.DropTable(
                name: "MLConformalMartingaleLogs");

            migrationBuilder.DropTable(
                name: "MLConsistencyModelLogs");

            migrationBuilder.DropTable(
                name: "MLContrastiveEncoder");

            migrationBuilder.DropTable(
                name: "MLCornishFisherLogs");

            migrationBuilder.DropTable(
                name: "MLCorrelationSurpriseLog");

            migrationBuilder.DropTable(
                name: "MLCorwinSchultzLogs");

            migrationBuilder.DropTable(
                name: "MLCounterfactualLogs");

            migrationBuilder.DropTable(
                name: "MLCqrLogs");

            migrationBuilder.DropTable(
                name: "MLCrcCalibration");

            migrationBuilder.DropTable(
                name: "MLCrcLogs");

            migrationBuilder.DropTable(
                name: "MLCrossSecMomentumLogs");

            migrationBuilder.DropTable(
                name: "MLCrossStrategyTransferLogs");

            migrationBuilder.DropTable(
                name: "MLCrownLogs");

            migrationBuilder.DropTable(
                name: "MLCurrencyPairGraph");

            migrationBuilder.DropTable(
                name: "MLDataCartographyLogs");

            migrationBuilder.DropTable(
                name: "MLDbscanClusterLogs");

            migrationBuilder.DropTable(
                name: "MLDccaLogs");

            migrationBuilder.DropTable(
                name: "MLDccGarchLogs");

            migrationBuilder.DropTable(
                name: "MLDesLogs");

            migrationBuilder.DropTable(
                name: "MLDirichletUncertaintyLog");

            migrationBuilder.DropTable(
                name: "MLDklLogs");

            migrationBuilder.DropTable(
                name: "MLDmlEffectLog");

            migrationBuilder.DropTable(
                name: "MLDmTestLogs");

            migrationBuilder.DropTable(
                name: "MLDqnLogs");

            migrationBuilder.DropTable(
                name: "MLDynamicFactorModelLogs");

            migrationBuilder.DropTable(
                name: "MLDynotearsLogs");

            migrationBuilder.DropTable(
                name: "MLEbmLogs");

            migrationBuilder.DropTable(
                name: "MLEceLog");

            migrationBuilder.DropTable(
                name: "MLEconomicImpactLog");

            migrationBuilder.DropTable(
                name: "MLEigenportfolioLogs");

            migrationBuilder.DropTable(
                name: "MLEmdDriftLog");

            migrationBuilder.DropTable(
                name: "MLEmdLogs");

            migrationBuilder.DropTable(
                name: "MLEnbPiLogs");

            migrationBuilder.DropTable(
                name: "MLEnsembleLearnerWeight");

            migrationBuilder.DropTable(
                name: "MLEntropyPoolingLogs");

            migrationBuilder.DropTable(
                name: "MLEstarLogs");

            migrationBuilder.DropTable(
                name: "MLEtsParams");

            migrationBuilder.DropTable(
                name: "MLEvalueLogs");

            migrationBuilder.DropTable(
                name: "MLEventStudyLogs");

            migrationBuilder.DropTable(
                name: "MLEvidentialLogs");

            migrationBuilder.DropTable(
                name: "MLEvidentialParams");

            migrationBuilder.DropTable(
                name: "MLEvtGpdLogs");

            migrationBuilder.DropTable(
                name: "MLEvtRiskEstimate");

            migrationBuilder.DropTable(
                name: "MLExp3Logs");

            migrationBuilder.DropTable(
                name: "MLExp4Logs");

            migrationBuilder.DropTable(
                name: "MLExperienceReplayEntry");

            migrationBuilder.DropTable(
                name: "MLFactorCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLFactorModelLogs");

            migrationBuilder.DropTable(
                name: "MLFamaMacBethLogs");

            migrationBuilder.DropTable(
                name: "MLFeatureNormStats");

            migrationBuilder.DropTable(
                name: "MLFederatedModelLogs");

            migrationBuilder.DropTable(
                name: "MLFlowMatchingLogs");

            migrationBuilder.DropTable(
                name: "MLFpcaLogs");

            migrationBuilder.DropTable(
                name: "MLFractionalDiffLogs");

            migrationBuilder.DropTable(
                name: "MLFuzzyRegimeMembership");

            migrationBuilder.DropTable(
                name: "MLGarchEvtCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLGarchMLogs");

            migrationBuilder.DropTable(
                name: "MLGarchModel");

            migrationBuilder.DropTable(
                name: "MLGasModelLogs");

            migrationBuilder.DropTable(
                name: "MLGaussianCopulaLogs");

            migrationBuilder.DropTable(
                name: "MLGemLogs");

            migrationBuilder.DropTable(
                name: "MLGjrGarchLogs");

            migrationBuilder.DropTable(
                name: "MLGlobalSurrogateLogs");

            migrationBuilder.DropTable(
                name: "MLGlostenMilgromLogs");

            migrationBuilder.DropTable(
                name: "MLGonzaloGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLGradientSaliencyLog");

            migrationBuilder.DropTable(
                name: "MLGramCharlierLogs");

            migrationBuilder.DropTable(
                name: "MLHamiltonRegimeLogs");

            migrationBuilder.DropTable(
                name: "MLHansenSkewedTLogs");

            migrationBuilder.DropTable(
                name: "MLHarRvLogs");

            migrationBuilder.DropTable(
                name: "MLHayashiYoshidaLogs");

            migrationBuilder.DropTable(
                name: "MLHjbLogs");

            migrationBuilder.DropTable(
                name: "MLHoeffdingTreeLogs");

            migrationBuilder.DropTable(
                name: "MLHotellingDriftLog");

            migrationBuilder.DropTable(
                name: "MLHrpAllocationLogs");

            migrationBuilder.DropTable(
                name: "MLHrpConvexLogs");

            migrationBuilder.DropTable(
                name: "MLHsicLingamLogs");

            migrationBuilder.DropTable(
                name: "MLHStatisticLog");

            migrationBuilder.DropTable(
                name: "MLHuberRegressionLogs");

            migrationBuilder.DropTable(
                name: "MLHyperbolicLogs");

            migrationBuilder.DropTable(
                name: "MLHyperparamPrior");

            migrationBuilder.DropTable(
                name: "MLIbEncoder");

            migrationBuilder.DropTable(
                name: "MLIcaLogs");

            migrationBuilder.DropTable(
                name: "MLInformationShareLogs");

            migrationBuilder.DropTable(
                name: "MLInputGradientNormLog");

            migrationBuilder.DropTable(
                name: "MLInstrumentalVarLogs");

            migrationBuilder.DropTable(
                name: "MLIntegratedGradientsLog");

            migrationBuilder.DropTable(
                name: "MLIntradaySeasonalityLogs");

            migrationBuilder.DropTable(
                name: "MLIsolationForestLogs");

            migrationBuilder.DropTable(
                name: "MLJohansenLogs");

            migrationBuilder.DropTable(
                name: "MLJsFeatureRanking");

            migrationBuilder.DropTable(
                name: "MLJumpTestLogs");

            migrationBuilder.DropTable(
                name: "MLKalmanCoefficientLog");

            migrationBuilder.DropTable(
                name: "MLKalmanEmLogs");

            migrationBuilder.DropTable(
                name: "MLLaplaceLogs");

            migrationBuilder.DropTable(
                name: "MLLassoGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLLassoVarLogs");

            migrationBuilder.DropTable(
                name: "MLLearnThenTestLogs");

            migrationBuilder.DropTable(
                name: "MLLedoitWolfLogs");

            migrationBuilder.DropTable(
                name: "MLLeverageCycleLogs");

            migrationBuilder.DropTable(
                name: "MLLevyProcessLogs");

            migrationBuilder.DropTable(
                name: "MLLimeExplanationLog");

            migrationBuilder.DropTable(
                name: "MLLimeLogs");

            migrationBuilder.DropTable(
                name: "MLLingamLogs");

            migrationBuilder.DropTable(
                name: "MLLiquidityRegimeAlert");

            migrationBuilder.DropTable(
                name: "MLLotteryTicketLog");

            migrationBuilder.DropTable(
                name: "MLLowRankLogs");

            migrationBuilder.DropTable(
                name: "MLLVaRLogs");

            migrationBuilder.DropTable(
                name: "MLMarchenkoPasturLogs");

            migrationBuilder.DropTable(
                name: "MLMarkovSwitchGarchLogs");

            migrationBuilder.DropTable(
                name: "MLMatrixProfileLogs");

            migrationBuilder.DropTable(
                name: "MLMaxEntLogs");

            migrationBuilder.DropTable(
                name: "MLMcdLogs");

            migrationBuilder.DropTable(
                name: "MLMcsLogs");

            migrationBuilder.DropTable(
                name: "MLMdnParams");

            migrationBuilder.DropTable(
                name: "MLMeanCvarLogs");

            migrationBuilder.DropTable(
                name: "MLMembershipInferenceLogs");

            migrationBuilder.DropTable(
                name: "MLMfboLogs");

            migrationBuilder.DropTable(
                name: "MLMfdfaLogs");

            migrationBuilder.DropTable(
                name: "MLMicropriceLogs");

            migrationBuilder.DropTable(
                name: "MLMidasLogs");

            migrationBuilder.DropTable(
                name: "MLMineLogs");

            migrationBuilder.DropTable(
                name: "MLMinTReconciliationLog");

            migrationBuilder.DropTable(
                name: "MLMiRedundancyLog");

            migrationBuilder.DropTable(
                name: "MLModelGoodnessOfFit");

            migrationBuilder.DropTable(
                name: "MLModelMagnitudeStats");

            migrationBuilder.DropTable(
                name: "MLMoeGatingLog");

            migrationBuilder.DropTable(
                name: "MLMondrianCalibration");

            migrationBuilder.DropTable(
                name: "MLMsvarLogs");

            migrationBuilder.DropTable(
                name: "MLNBeatsDecompLogs");

            migrationBuilder.DropTable(
                name: "MLNcsnLogs");

            migrationBuilder.DropTable(
                name: "MLNeuralGrangerLogs");

            migrationBuilder.DropTable(
                name: "MLNeuralProcessEncoder");

            migrationBuilder.DropTable(
                name: "MLNflTestLogs");

            migrationBuilder.DropTable(
                name: "MLNmfLatentBasis");

            migrationBuilder.DropTable(
                name: "MLNmfLogs");

            migrationBuilder.DropTable(
                name: "MLNode2VecEmbeddingLogs");

            migrationBuilder.DropTable(
                name: "MLNode2VecLogs");

            migrationBuilder.DropTable(
                name: "MLNowcastLogs");

            migrationBuilder.DropTable(
                name: "MLNsgaIILogs");

            migrationBuilder.DropTable(
                name: "MLOfiLogs");

            migrationBuilder.DropTable(
                name: "MLOhlcVolatilityLogs");

            migrationBuilder.DropTable(
                name: "MLOmdLogs");

            migrationBuilder.DropTable(
                name: "MLOmegaCalmarLogs");

            migrationBuilder.DropTable(
                name: "MLOosEquityCurveSnapshot");

            migrationBuilder.DropTable(
                name: "MLOptimalStoppingLog");

            migrationBuilder.DropTable(
                name: "MLOptimizationParetoFront");

            migrationBuilder.DropTable(
                name: "MLOuHalfLifeLogs");

            migrationBuilder.DropTable(
                name: "MLPackNetLogs");

            migrationBuilder.DropTable(
                name: "MLPartialDependenceBaseline");

            migrationBuilder.DropTable(
                name: "MLParticleFilterLogs");

            migrationBuilder.DropTable(
                name: "MLPassiveAggressiveLogs");

            migrationBuilder.DropTable(
                name: "MLPathSignatureLogs");

            migrationBuilder.DropTable(
                name: "MLPcaWhiteningLog");

            migrationBuilder.DropTable(
                name: "MLPcCausalLog");

            migrationBuilder.DropTable(
                name: "MLPcmciLogs");

            migrationBuilder.DropTable(
                name: "MLPerformanceDecompositionLogs");

            migrationBuilder.DropTable(
                name: "MLPersistentLaplacianLogs");

            migrationBuilder.DropTable(
                name: "MLPredictionScorePsiLog");

            migrationBuilder.DropTable(
                name: "MLPriceImpactDecayLogs");

            migrationBuilder.DropTable(
                name: "MLProfitFactorLogs");

            migrationBuilder.DropTable(
                name: "MLQuadraticCovariationLogs");

            migrationBuilder.DropTable(
                name: "MLQuantileCoverageLog");

            migrationBuilder.DropTable(
                name: "MLQuantizationLogs");

            migrationBuilder.DropTable(
                name: "MLQueryByCommitteeLogs");

            migrationBuilder.DropTable(
                name: "MLRademacherLogs");

            migrationBuilder.DropTable(
                name: "MLRapsCalibration");

            migrationBuilder.DropTable(
                name: "MLRddLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedEigenvolLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedKernelLogs");

            migrationBuilder.DropTable(
                name: "MLRealizedQuarticityLogs");

            migrationBuilder.DropTable(
                name: "MLRealNvpLogs");

            migrationBuilder.DropTable(
                name: "MLRegimeCAPMLogs");

            migrationBuilder.DropTable(
                name: "MLRegimeFeatureImportance");

            migrationBuilder.DropTable(
                name: "MLRegimePrototype");

            migrationBuilder.DropTable(
                name: "MLRegimeSynchronyLog");

            migrationBuilder.DropTable(
                name: "MLRenyiDivergenceLog");

            migrationBuilder.DropTable(
                name: "MLReptileLogs");

            migrationBuilder.DropTable(
                name: "MLReservoirEncoder");

            migrationBuilder.DropTable(
                name: "MLRetrainingScheduleLogs");

            migrationBuilder.DropTable(
                name: "MLRetrogradeFalsePatternLog");

            migrationBuilder.DropTable(
                name: "MLRobustCovLogs");

            migrationBuilder.DropTable(
                name: "MLRollSpreadLogs");

            migrationBuilder.DropTable(
                name: "MLRoroIndexLogs");

            migrationBuilder.DropTable(
                name: "MLRotationForestLogs");

            migrationBuilder.DropTable(
                name: "MLRoughVolLogs");

            migrationBuilder.DropTable(
                name: "MLRpcaAnomalyLog");

            migrationBuilder.DropTable(
                name: "MLSacLogs");

            migrationBuilder.DropTable(
                name: "MLSageLogs");

            migrationBuilder.DropTable(
                name: "MLSamLogs");

            migrationBuilder.DropTable(
                name: "MLSarimaNeuralLogs");

            migrationBuilder.DropTable(
                name: "MLSchrodingerBridgeLogs");

            migrationBuilder.DropTable(
                name: "MLScorecardLogs");

            migrationBuilder.DropTable(
                name: "MLSemiparametricVolLogs");

            migrationBuilder.DropTable(
                name: "MLSemivarianceLogs");

            migrationBuilder.DropTable(
                name: "MLSessionPlattCalibration");

            migrationBuilder.DropTable(
                name: "MLSgldLogs");

            migrationBuilder.DropTable(
                name: "MLShapDriftLog");

            migrationBuilder.DropTable(
                name: "MLShapInteractionLog");

            migrationBuilder.DropTable(
                name: "MLSignatureTransformLogs");

            migrationBuilder.DropTable(
                name: "MLSinkhornLogs");

            migrationBuilder.DropTable(
                name: "MLSkewRpLogs");

            migrationBuilder.DropTable(
                name: "MLSlicedWassersteinLogs");

            migrationBuilder.DropTable(
                name: "MLSnapshotCheckpoint");

            migrationBuilder.DropTable(
                name: "MLSomModel");

            migrationBuilder.DropTable(
                name: "MLSparseEncoder");

            migrationBuilder.DropTable(
                name: "MLSparsePcaLogs");

            migrationBuilder.DropTable(
                name: "MLSpdNetLogs");

            migrationBuilder.DropTable(
                name: "MLSpectralGraphLogs");

            migrationBuilder.DropTable(
                name: "MLSpilloverLogs");

            migrationBuilder.DropTable(
                name: "MLSplLogs");

            migrationBuilder.DropTable(
                name: "MLSprtLogs");

            migrationBuilder.DropTable(
                name: "MLSsaComponentLogs");

            migrationBuilder.DropTable(
                name: "MLSsaLogs");

            migrationBuilder.DropTable(
                name: "MLStatArbLogs");

            migrationBuilder.DropTable(
                name: "MLStatArbPcaLogs");

            migrationBuilder.DropTable(
                name: "MLStlDecompositionLogs");

            migrationBuilder.DropTable(
                name: "MLStochVolLogs");

            migrationBuilder.DropTable(
                name: "MLStructuredPruningLogs");

            migrationBuilder.DropTable(
                name: "MLSuperstatisticsLogs");

            migrationBuilder.DropTable(
                name: "MLSurvivalLogs");

            migrationBuilder.DropTable(
                name: "MLSurvivalModel");

            migrationBuilder.DropTable(
                name: "MLSvarLogs");

            migrationBuilder.DropTable(
                name: "MLSvgdLogs");

            migrationBuilder.DropTable(
                name: "MLSymbolicRegressionLogs");

            migrationBuilder.DropTable(
                name: "MLSyntheticControlLogs");

            migrationBuilder.DropTable(
                name: "MLTarchLogs");

            migrationBuilder.DropTable(
                name: "MLTaskArithmeticLogs");

            migrationBuilder.DropTable(
                name: "MLTcavLogs");

            migrationBuilder.DropTable(
                name: "MLTdaLogs");

            migrationBuilder.DropTable(
                name: "MLTermStructVrpLogs");

            migrationBuilder.DropTable(
                name: "MLTgcnLogs");

            migrationBuilder.DropTable(
                name: "MLTransferEntropyLogs");

            migrationBuilder.DropTable(
                name: "MLTsrvLogs");

            migrationBuilder.DropTable(
                name: "MLTtaInferenceLogs");

            migrationBuilder.DropTable(
                name: "MLTuckerLogs");

            migrationBuilder.DropTable(
                name: "MLUniversalPortfolioLogs");

            migrationBuilder.DropTable(
                name: "MLVennAbersCalibration");

            migrationBuilder.DropTable(
                name: "MLVmdLogs");

            migrationBuilder.DropTable(
                name: "MLVpinLogs");

            migrationBuilder.DropTable(
                name: "MLVrpLogs");

            migrationBuilder.DropTable(
                name: "MLWassersteinLogs");

            migrationBuilder.DropTable(
                name: "MLWaveletCoherenceLogs");

            migrationBuilder.DropTable(
                name: "MLWeibullTteLogs");

            migrationBuilder.DropTable(
                name: "MLWganCheckpoint");

            migrationBuilder.DropTable(
                name: "MLZumbachEffectLogs");

            migrationBuilder.AddColumn<long>(
                name: "OpenOrderId",
                table: "Position",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Position",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Order",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "MLModel",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateIndex(
                name: "IX_Position_OpenOrderId",
                table: "Position",
                column: "OpenOrderId",
                unique: true,
                filter: "\"OpenOrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Position_OpenOrderId",
                table: "Position");

            migrationBuilder.DropColumn(
                name: "OpenOrderId",
                table: "Position");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Position");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "MLModel");

            migrationBuilder.CreateTable(
                name: "MLA2cLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActorEntropyMean = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriticLossMean = table.Column<double>(type: "double precision", nullable: false),
                    Episodes = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanReturn = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLA2cLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAbsorptionRatioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbsorptionRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KComponents = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousAbsorptionRatio = table.Column<double>(type: "double precision", nullable: false),
                    RatioChange = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SystemicRiskHigh = table.Column<bool>(type: "boolean", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAbsorptionRatioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAcdDurationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    ExpectedDuration = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAcdDurationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLActComplexityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActComplexityScore = table.Column<double>(type: "double precision", nullable: false),
                    CompressedSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NormalizedComplexity = table.Column<double>(type: "double precision", nullable: false),
                    OriginalSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLActComplexityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLActivationMaxLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LearnerIndex = table.Column<int>(type: "integer", nullable: false),
                    MaxActivation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MaxActivationFeaturesJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLActivationMaxLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLActivationMaxLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLAdaptiveConformalLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AciAlpha = table.Column<double>(type: "double precision", nullable: false),
                    AdaptationRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionIntervalHigh = table.Column<double>(type: "double precision", nullable: false),
                    PredictionIntervalLow = table.Column<double>(type: "double precision", nullable: false),
                    RecentCoverage = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveConformalLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdaptiveDropoutLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Accuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EpochNumber = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LayerDropoutRatesJson = table.Column<string>(type: "text", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ValidationLoss = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveDropoutLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdaptiveWindowConfig",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BestCvLogLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CvScoresJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OptimalWindowBars = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdaptiveWindowConfig", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdversarialRobustnessLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AccuracyDrop = table.Column<double>(type: "double precision", nullable: false),
                    BaseAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Epsilon = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerturbedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    SamplesTested = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdversarialRobustnessLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAdversarialRobustnessLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLAdversarialValidationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdvAuroc = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TestSamples = table.Column<int>(type: "integer", nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TopFeaturesJson = table.Column<string>(type: "text", nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdversarialValidationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAdwinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentWindowSize = table.Column<int>(type: "integer", nullable: false),
                    DeltaParam = table.Column<double>(type: "double precision", nullable: false),
                    DriftCount = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanAccuracyNew = table.Column<double>(type: "double precision", nullable: false),
                    MeanAccuracyOld = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAdwinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AleValuesJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GridPoints = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MaxAbsEffect = table.Column<double>(type: "double precision", nullable: false),
                    MaxAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    MeanAbsAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    MeanAbsEffect = table.Column<double>(type: "double precision", nullable: false),
                    MinAleEffect = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantileCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAlmgrenChrissLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExecutionRisk = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ExpectedExecutionCost = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OptimalExecutionIntervals = table.Column<int>(type: "integer", nullable: false),
                    OptimalUrgency = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermanentImpactParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RiskAversion = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TemporaryImpactParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAlmgrenChrissLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAlphaStableLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaIndex = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BetaSkewness = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeltaLocation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    EstimationMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GammaScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GoodnessFitKs = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TailVaR99 = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAlphaStableLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAmihudLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    IlliquidityAlert = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PercentileRank = table.Column<double>(type: "double precision", nullable: false),
                    SampleDays = table.Column<int>(type: "integer", nullable: false),
                    StdIlliquidity = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAmihudLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLArchLmTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HasRemainingArch = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    LmStatistic = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLArchLmTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLAttentionWeightLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AttentionWeightsJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TopFeatureIdx = table.Column<int>(type: "integer", nullable: false),
                    TopFeatureWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLAttentionWeightLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLAttentionWeightLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBaldLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AleatoricEntropy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EpistemicEntropy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxBaldScore = table.Column<double>(type: "double precision", nullable: false),
                    MeanBaldScore = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesEvaluated = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TopUncertaintySample = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBaldLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBaldLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLBarlowTwinsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrossCorrelationDiagMean = table.Column<double>(type: "double precision", nullable: false),
                    CrossCorrelationOffDiagMean = table.Column<double>(type: "double precision", nullable: false),
                    Epochs = table.Column<int>(type: "integer", nullable: false),
                    InvarianceLoss = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LatentDimensions = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedundancyLoss = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalLoss = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBarlowTwinsLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBarlowTwinsLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLBatesCalibLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CalibrationError = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HestonVol = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JumpIntensity = table.Column<double>(type: "double precision", nullable: false),
                    JumpMeanSize = table.Column<double>(type: "double precision", nullable: false),
                    JumpVolatility = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBatesCalibLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBayesFactorLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    HypothesisLabel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogBayesFactor = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBayesFactorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBayesThresholdLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedImprovement = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    GPMeanAtOptimum = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OptimalThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrialsCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBayesThresholdLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBayesThresholdLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBetaVaeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BetaValue = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DisentanglementScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KlDivergence = table.Column<double>(type: "double precision", nullable: false),
                    LatentDimensions = table.Column<int>(type: "integer", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanLatentVariance = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBetaVaeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBipowerVariationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BipowerVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContinuousVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsJumpDay = table.Column<bool>(type: "boolean", nullable: false),
                    JumpComponent = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    JumpRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TriPowerQuarticity = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBipowerVariationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBivariateEvtLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChiStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExtremeCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TailDependenceLower = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    TailDependenceUpper = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ThresholdUsed = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBivariateEvtLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBlackLittermanLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BlendedReturn = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EquilibriumReturn = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OptimalWeight = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TauParam = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ViewConfidence = table.Column<double>(type: "double precision", nullable: false),
                    ViewReturn = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBlackLittermanLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBocpdChangePoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ChangePointAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxPosterior = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunLength = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBocpdChangePoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBocpdChangePoint_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBocpdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChangePointProbability = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CredibleIntervalHigh = table.Column<int>(type: "integer", nullable: false),
                    CredibleIntervalLow = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PriorHazardRate = table.Column<double>(type: "double precision", nullable: false),
                    RunLengthMode = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBocpdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLBootstrapEnsemble",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BootstrapAccuracy = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    DrawWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NumDraws = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBootstrapEnsemble", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLBootstrapEnsemble_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLBvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionNumber = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    InsampleMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MnPriorLambda = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MnPriorTightness = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OosDirectionAccuracy = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    VarOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLBvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLC51Distribution",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AtomProbsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NumAtoms = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    VMax = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    VMin = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Var95 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLC51Distribution", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLC51Distribution_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCandleBertEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    EncoderWeightsJson = table.Column<string>(type: "text", nullable: false),
                    HiddenDim = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NumLayers = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCandleBertEncoder", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCandleBertEncoder_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCandlestickLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DojiCount = table.Column<int>(type: "integer", nullable: false),
                    EngulfingCount = table.Column<int>(type: "integer", nullable: false),
                    HammerCount = table.Column<int>(type: "integer", nullable: false),
                    InsideBarCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MostRecentPattern = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ThreeBarReversalCount = table.Column<int>(type: "integer", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCandlestickLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCarrLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionalRange = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RangeForecast = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCarrLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCarryDecompLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CarryComponent = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrashRiskPremium = table.Column<double>(type: "double precision", nullable: false),
                    ExchangeRateComponent = table.Column<double>(type: "double precision", nullable: false),
                    InterestDifferential = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCarryDecompLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCashSelectionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BestValidationAccuracy = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    BicPenalty = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CandidateResultsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SelectedTrainerKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCashSelectionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCashSelectionLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCausalImpactLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbsoluteEffect = table.Column<double>(type: "double precision", nullable: false),
                    ActualMean = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CounterfactualMean = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeEffect = table.Column<double>(type: "double precision", nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostPeriodBars = table.Column<int>(type: "integer", nullable: false),
                    PrePeriodBars = table.Column<int>(type: "integer", nullable: false),
                    ProbabilityOfCausalEffect = table.Column<double>(type: "double precision", nullable: false),
                    RelativeEffect = table.Column<double>(type: "double precision", nullable: false),
                    SignificantImpact = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCausalImpactLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCipLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CipBasis = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ForwardPremium = table.Column<double>(type: "double precision", nullable: false),
                    InterestDifferential = table.Column<double>(type: "double precision", nullable: false),
                    IsArbitrageable = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCipLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLClarkWestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BenchmarkMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ChallengerMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CwStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsChallengerBetter = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId1 = table.Column<long>(type: "bigint", nullable: false),
                    MLModelId2 = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLClarkWestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCmimFeatureRankingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionalMi = table.Column<double>(type: "double precision", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSelected = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionRank = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCmimFeatureRankingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCmimFeatureRankingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCojumpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CojumpCount = table.Column<int>(type: "integer", nullable: false),
                    CojumpZScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedCojumpRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IndividualJumpRate1 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IndividualJumpRate2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    ObservedCojumpRate = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCojumpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalAnomalyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ConformalPValue = table.Column<double>(type: "double precision", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeaturesJson = table.Column<string>(type: "text", nullable: false),
                    IsAnomaly = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalAnomalyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalAnomalyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalEfficiencyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    AvgSetSize = table.Column<double>(type: "double precision", nullable: false),
                    BaselineSetSize = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EfficiencyRatio = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalEfficiencyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLConformalEfficiencyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLConformalMartingaleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnomalyDetected = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogMartingaleValue = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MartingaleValue = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepsProcessed = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConformalMartingaleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLConsistencyModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BestNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsistencyImprovement = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanConsistencyLoss = table.Column<double>(type: "double precision", nullable: false),
                    NoiseLevels = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLConsistencyModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLContrastiveEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContrastiveLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectionBiasJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ProjectionWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLContrastiveEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCornishFisherLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CfES99 = table.Column<double>(type: "double precision", nullable: false),
                    CfVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExcessKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    GaussianVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Skewness = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCornishFisherLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCorrelationSurpriseLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    BaselineRho = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentRho = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurpriseZScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol1 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorrelationSurpriseLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCorwinSchultzLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsLiquid = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpreadEstimate = table.Column<double>(type: "double precision", nullable: false),
                    SpreadPct = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WindowBars = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCorwinSchultzLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCounterfactualLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanFeaturesChanged = table.Column<double>(type: "double precision", nullable: false),
                    MinFeaturesChanged = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesAnalyzed = table.Column<int>(type: "integer", nullable: false),
                    SuccessRate = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCounterfactualLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCqrLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionalCoverage = table.Column<double>(type: "double precision", nullable: false),
                    CoverageProbability = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanIntervalWidth = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantileHigh = table.Column<double>(type: "double precision", nullable: false),
                    QuantileLow = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCqrLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCqrLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLCrcCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LambdaHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LossFunction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrcCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrcCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLCrcLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibrationSetSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoverageAchieved = table.Column<double>(type: "double precision", nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LambdaThreshold = table.Column<double>(type: "double precision", nullable: false),
                    MeanSetSize = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskBound = table.Column<double>(type: "double precision", nullable: false),
                    RiskFunctionName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WorstCaseRisk = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrcLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrcLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLCrossSecMomentumLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsTopQuartile = table.Column<bool>(type: "boolean", nullable: false),
                    MomentumRank = table.Column<double>(type: "double precision", nullable: false),
                    MomentumScore = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativeReturn = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrossSecMomentumLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrossStrategyTransferLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccuracyGap = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DonorModelId = table.Column<long>(type: "bigint", nullable: false),
                    DonorTimeframe = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransferRecommended = table.Column<bool>(type: "boolean", nullable: false),
                    WeightSimilarity = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrossStrategyTransferLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLCrownLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CertifiedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    CertifiedCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrownTightnessRatio = table.Column<double>(type: "double precision", nullable: false),
                    IbpLowerBound = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanLipschitzBound = table.Column<double>(type: "double precision", nullable: false),
                    MinFlipEpsilon = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerturbationThreshold = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalTestSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCrownLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLCrownLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLCurrencyPairGraph",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Correlation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EdgeWeight = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLCurrencyPairGraph", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDataCartographyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Cartography = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleIndex = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Variability = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDataCartographyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDbscanClusterLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClusterCount = table.Column<int>(type: "integer", nullable: false),
                    ClusterSizesJson = table.Column<string>(type: "text", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Epsilon = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MinPoints = table.Column<int>(type: "integer", nullable: false),
                    NoisePointCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDbscanClusterLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDccaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrossoverScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DccaScale16 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DccaScale4 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DccaScale64 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LongRangeCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanDccaCoefficient = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDccaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDccGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    BetaParam = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationChange = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DynamicCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UnconditionalCorrelation = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDccGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDesLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompetenceThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GlobalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LocalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NeighborhoodSize = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedLearners = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    TotalLearners = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDesLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDirichletUncertaintyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AvgAleatoricUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    AvgConcentration = table.Column<double>(type: "double precision", nullable: false),
                    AvgEpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDirichletUncertaintyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLDirichletUncertaintyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLDklLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InducingPoints = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KernelLengthScale = table.Column<double>(type: "double precision", nullable: false),
                    KernelOutputScale = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    NllScore = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VarianceMean = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDklLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDmlEffectLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CausalEffect = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FoldCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    StandardError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDmlEffectLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLDmlEffectLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLDmTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DmStatistic = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ModelAId = table.Column<long>(type: "bigint", nullable: false),
                    ModelAIsSuperior = table.Column<bool>(type: "boolean", nullable: false),
                    ModelBId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDmTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDqnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BestAction = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Episodes = table.Column<int>(type: "integer", nullable: false),
                    EpsilonFinal = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanReward = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDqnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDynamicFactorModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommonVarianceFraction = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Factor1Loading = table.Column<double>(type: "double precision", nullable: false),
                    Factor2Loading = table.Column<double>(type: "double precision", nullable: false),
                    Factor3Loading = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDynamicFactorModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLDynotearsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AcyclicityResidual = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContemporaneousEdgesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LaggedEdgesJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    MaxLagOrder = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SparsityLevel = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLDynotearsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEbmLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ContrastiveDivergence = table.Column<double>(type: "double precision", nullable: false),
                    EnergyGap = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanEnergyFantasy = table.Column<double>(type: "double precision", nullable: false),
                    MeanEnergyReal = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEbmLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEceLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Ece = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EceEwma = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NumBins = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEceLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEceLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEconomicImpactLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EconomicEventId = table.Column<long>(type: "bigint", nullable: true),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AccuracyAfter = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyBefore = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiffInDiff = table.Column<double>(type: "double precision", nullable: false),
                    EventType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEconomicImpactLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEconomicImpactLog_EconomicEvent_EconomicEventId",
                        column: x => x.EconomicEventId,
                        principalTable: "EconomicEvent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MLEconomicImpactLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEigenportfolioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaExplainedVar = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiedlerPortfolioJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IdiosyncraticRisk = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MarketBetaExposure = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Top3EigenvalueShareJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEigenportfolioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEmdDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BaselinePeriodDays = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentWindowDays = table.Column<int>(type: "integer", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    WassersteinDistance = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEmdDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEmdDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Imf1Energy = table.Column<double>(type: "double precision", nullable: false),
                    Imf2Energy = table.Column<double>(type: "double precision", nullable: false),
                    Imf3Energy = table.Column<double>(type: "double precision", nullable: false),
                    InstantaneousFreq = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrendEnergy = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEmdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEnbPiLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSetSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoverageH1 = table.Column<double>(type: "double precision", nullable: false),
                    CoverageH3 = table.Column<double>(type: "double precision", nullable: false),
                    CoverageH5 = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanIntervalWidthH1 = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidthH3 = table.Column<double>(type: "double precision", nullable: false),
                    MeanIntervalWidthH5 = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEnbPiLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEnbPiLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLEnsembleLearnerWeight",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CumulativeLogWealth = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LearnerIndex = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RollingBrierScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RollingPredictions = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEnsembleLearnerWeight", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEnsembleLearnerWeight_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLEntropyPoolingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KlDivergence = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PosteriorWeight = table.Column<double>(type: "double precision", nullable: false),
                    PriorWeight = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ViewStrength = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEntropyPoolingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEstarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Equilibrium = table.Column<double>(type: "double precision", nullable: false),
                    EstarGamma = table.Column<double>(type: "double precision", nullable: false),
                    IsCointegrated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEstarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEtsParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Alpha = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    ValidationMse = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEtsParams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvalueLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentEValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogEValue = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignificanceReached = table.Column<bool>(type: "boolean", nullable: false),
                    SignificanceThreshold = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvalueLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEventStudyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AbnormalReturn = table.Column<double>(type: "double precision", nullable: false),
                    Car = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEventStudyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvidentialLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AleatoricUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    BeliefMass = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DirichletAlpha0 = table.Column<double>(type: "double precision", nullable: false),
                    DirichletAlpha1 = table.Column<double>(type: "double precision", nullable: false),
                    EpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalUncertainty = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvidentialLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvidentialParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AleatoricUnc = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EpistemicUnc = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GammaMean = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Nu = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvidentialParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEvidentialParams_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLEvtGpdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CVaR99 = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExceedanceCount = table.Column<int>(type: "integer", nullable: false),
                    GpdScale = table.Column<double>(type: "double precision", nullable: false),
                    GpdShape = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TailIndex = table.Column<double>(type: "double precision", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VaR99 = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvtGpdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLEvtRiskEstimate",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CVaR99 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GpdScale = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    GpdShape = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    TailSamples = table.Column<int>(type: "integer", nullable: false),
                    TailThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    Var99 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLEvtRiskEstimate", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLEvtRiskEstimate_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLExp3Logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArmCount = table.Column<int>(type: "integer", nullable: false),
                    ArmWeightsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CumulativeRegret = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LearningRate = table.Column<double>(type: "double precision", nullable: false),
                    MixingCoeff = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedArm = table.Column<int>(type: "integer", nullable: false),
                    SelectedArmProbability = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalRounds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExp3Logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLExp4Logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArmCount = table.Column<int>(type: "integer", nullable: false),
                    ArmWeightsJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CumulativeRegret = table.Column<double>(type: "double precision", nullable: false),
                    EstimatedReward = table.Column<double>(type: "double precision", nullable: false),
                    ExpertAgreementRate = table.Column<double>(type: "double precision", nullable: false),
                    ExpertCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedArm = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalRounds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExp4Logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLExperienceReplayEntry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ActualDirection = table.Column<int>(type: "integer", nullable: false),
                    FeaturesJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictedProb = table.Column<double>(type: "double precision", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    WasCorrect = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLExperienceReplayEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLExperienceReplayEntry_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLFactorCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FactorCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    FactorLoading = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JointTailProb = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SystemicCrashProb = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFactorCopulaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFactorModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlphaReturn = table.Column<double>(type: "double precision", nullable: false),
                    CarryBeta = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IdiosyncraticReturn = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MomentumBeta = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RSquared = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SystematicReturn = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VolatilityBeta = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFactorModelLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFactorModelLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLFamaMacBethLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FactorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodCount = table.Column<int>(type: "integer", nullable: false),
                    RiskPremium = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TStatistic = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFamaMacBethLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFeatureNormStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeansJson = table.Column<string>(type: "text", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    StdsJson = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFeatureNormStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFederatedModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AggregatedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ClientCount = table.Column<int>(type: "integer", nullable: false),
                    ClientWeightsJson = table.Column<string>(type: "text", nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergenceReached = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoundNumber = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFederatedModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFlowMatchingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AugmentedSamplesGenerated = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanFeatureDelta = table.Column<double>(type: "double precision", nullable: false),
                    MeanVelocityMagnitude = table.Column<double>(type: "double precision", nullable: false),
                    MmdSyntheticVsReal = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QualityAcceptable = table.Column<bool>(type: "boolean", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false),
                    TrainingLoss = table.Column<double>(type: "double precision", nullable: false),
                    VelocityCurvature = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFlowMatchingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFpcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BasisDimension = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrossingRate = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    FirstFpcVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    Fpc1CoefficientsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanFunctionMean = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecondFpcVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFpcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFractionalDiffLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdfPValue = table.Column<double>(type: "double precision", nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationWithOriginal = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MemoryRetained = table.Column<double>(type: "double precision", nullable: false),
                    OptimalD = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFractionalDiffLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLFuzzyRegimeMembership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BlendedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RangingWeight = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrendingWeight = table.Column<double>(type: "double precision", nullable: false),
                    VolatileWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLFuzzyRegimeMembership", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLFuzzyRegimeMembership_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchEvtCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CopulaCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JointES99 = table.Column<double>(type: "double precision", nullable: false),
                    JointVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchEvtCopulaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchMLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionalVarianceForecast = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GarchMAlpha = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GarchMBeta = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GarchMOmega = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsPositiveRiskPremium = table.Column<bool>(type: "boolean", nullable: false),
                    LambdaTStatistic = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictedRiskPremium = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RiskPremiumLambda = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchMLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGarchModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Alpha = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    Beta = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    FitSamples = table.Column<int>(type: "integer", nullable: false),
                    FittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 6, nullable: false),
                    Omega = table.Column<double>(type: "double precision", precision: 18, scale: 12, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGarchModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGasModelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    Beta = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ForecastedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Omega = table.Column<double>(type: "double precision", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParameterStability = table.Column<double>(type: "double precision", nullable: false),
                    ScoreGradient = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeVaryingThreshold = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGasModelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGaussianCopulaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CopulaAicScore = table.Column<double>(type: "double precision", nullable: false),
                    CopulaCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JointLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MarginalKsStatistic = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TailDependenceLower = table.Column<double>(type: "double precision", nullable: false),
                    TailDependenceUpper = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGaussianCopulaLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLGaussianCopulaLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLGemLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EpisodeSize = table.Column<int>(type: "integer", nullable: false),
                    GradientConflicts = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanGradientCosine = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGemLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGjrGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaParam = table.Column<double>(type: "double precision", nullable: false),
                    BetaParam = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionalVolatility = table.Column<double>(type: "double precision", nullable: false),
                    GammaParam = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LeverageEffect = table.Column<double>(type: "double precision", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OmegaParam = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGjrGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGlobalSurrogateLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgreementRate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FidelityAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurrogateAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    SurrogateDepth = table.Column<int>(type: "integer", nullable: false),
                    SurrogateLeaves = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    TopSplitFeature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGlobalSurrogateLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGlostenMilgromLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AdverseSelectionComponent = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InventoryCost = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OrderProcessingCost = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalSpread = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGlostenMilgromLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGonzaloGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CointegrationPair = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GgContribution = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsLeader = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VecmAlpha = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGonzaloGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLGradientSaliencyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    L2ShiftFromPrior = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaliencyVectorJson = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGradientSaliencyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLGradientSaliencyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLGramCharlierLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GcCvar95 = table.Column<double>(type: "double precision", nullable: false),
                    GcKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    GcLogLik = table.Column<double>(type: "double precision", nullable: false),
                    GcSkewness = table.Column<double>(type: "double precision", nullable: false),
                    GcVar95 = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLGramCharlierLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHamiltonRegimeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DominantRegime = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProbRegime0 = table.Column<double>(type: "double precision", nullable: false),
                    ProbRegime1 = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Transition00 = table.Column<double>(type: "double precision", nullable: false),
                    Transition11 = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHamiltonRegimeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHansenSkewedTLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkewnessParam = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TailParam = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHansenSkewedTLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHarRvLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ForecastedRv = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBeta0 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBetaDaily = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBetaMonthly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    HarBetaWeekly = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    InsampleRmse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedRvDaily = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHarRvLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHayashiYoshidaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EppsRatio = table.Column<double>(type: "double precision", nullable: false),
                    HayashiYoshidaCorr = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LagSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeadLagSeconds = table.Column<double>(type: "double precision", nullable: false),
                    LeadSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHayashiYoshidaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHjbLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstimatedDrift = table.Column<double>(type: "double precision", nullable: false),
                    EstimatedVolatility = table.Column<double>(type: "double precision", nullable: false),
                    ExpectedUtility = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MertonFraction = table.Column<double>(type: "double precision", nullable: false),
                    OptimalPositionSize = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PdeResidualSteps = table.Column<int>(type: "integer", nullable: false),
                    RiskAversionGamma = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransactionCostPenalty = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHjbLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHjbLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLHoeffdingTreeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HoeffdingDelta = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LeafCount = table.Column<int>(type: "integer", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SplitCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    TreeDepth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHoeffdingTreeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHotellingDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriticalValue = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TSquaredStat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHotellingDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHotellingDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLHrpAllocationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AllocationJson = table.Column<string>(type: "text", nullable: false),
                    ClusterAssignment = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HrpWeight = table.Column<double>(type: "double precision", nullable: false),
                    InverseVarianceWeight = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHrpAllocationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHrpConvexLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClusteringLambda = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvexCluster = table.Column<int>(type: "integer", nullable: false),
                    DendrogramLevel = table.Column<int>(type: "integer", nullable: false),
                    HrpWeight = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHrpConvexLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHsicLingamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CausalEdgesJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FullyLinearDag = table.Column<bool>(type: "boolean", nullable: false),
                    HsicKernelBandwidth = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LinearEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    MaxHsicStatistic = table.Column<double>(type: "double precision", nullable: false),
                    MeanHsicStatistic = table.Column<double>(type: "double precision", nullable: false),
                    NonlinearEdgeCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalEdgeCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHsicLingamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHStatisticLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureNameA = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FeatureNameB = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HStatistic = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaterialisedAsProduct = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHStatisticLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLHStatisticLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLHuberRegressionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HuberDelta = table.Column<double>(type: "double precision", nullable: false),
                    ImprovementRatio = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutlierCount = table.Column<int>(type: "integer", nullable: false),
                    RmseOls = table.Column<double>(type: "double precision", nullable: false),
                    RmseRobust = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHuberRegressionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHyperbolicLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DistanceToOrigin = table.Column<double>(type: "double precision", nullable: false),
                    EmbeddingDimension = table.Column<int>(type: "integer", nullable: false),
                    EmbeddingLoss = table.Column<double>(type: "double precision", nullable: false),
                    EmbeddingVectorJson = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    HierarchyScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDominantNode = table.Column<bool>(type: "boolean", nullable: false),
                    MeanHyperbolicDistance = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHyperbolicLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLHyperparamPrior",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BestConfigJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BestObjective = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    GoodFraction = table.Column<double>(type: "double precision", precision: 5, scale: 4, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ObservationsJson = table.Column<string>(type: "text", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TotalObservations = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLHyperparamPrior", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIbEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Beta = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    MutualInfoZX = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    MutualInfoZY = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIbEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergedIterations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    MeanNegentropy = table.Column<double>(type: "double precision", nullable: false),
                    MixingMatrixNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLInformationShareLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommonFactor = table.Column<double>(type: "double precision", nullable: false),
                    ComparedToSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrossCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    IdiosyncraticVariance = table.Column<double>(type: "double precision", nullable: false),
                    InformationShare = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDominantSource = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnVariance = table.Column<double>(type: "double precision", nullable: false),
                    SignalVariance = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInformationShareLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLInputGradientNormLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GradientNormsJson = table.Column<string>(type: "text", nullable: false),
                    HighGradientFeatureIdx = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxGradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PenaltyTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInputGradientNormLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLInputGradientNormLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLInstrumentalVarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstStageFStat = table.Column<double>(type: "double precision", nullable: false),
                    FirstStageR2 = table.Column<double>(type: "double precision", nullable: false),
                    InstrumentName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LateEstimate = table.Column<double>(type: "double precision", nullable: false),
                    LateStdError = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SarganPValue = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLInstrumentalVarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIntegratedGradientsLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AttributionsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    BaselineScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntegrationSteps = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionScore = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIntegratedGradientsLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLIntegratedGradientsLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLIntradaySeasonalityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FffCoeff1 = table.Column<double>(type: "double precision", nullable: false),
                    FffCoeff2 = table.Column<double>(type: "double precision", nullable: false),
                    HourOfDay = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonalVolFactor = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolumeClockBar = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIntradaySeasonalityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLIsolationForestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanAnomalyScore = table.Column<double>(type: "double precision", nullable: false),
                    NumTrees = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLIsolationForestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLJohansenLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    Beta1 = table.Column<double>(type: "double precision", nullable: false),
                    Beta2 = table.Column<double>(type: "double precision", nullable: false),
                    CointegrationRank = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriticalValue95 = table.Column<double>(type: "double precision", nullable: false),
                    IsCointegrated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxEigenStatistic = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TraceStatistic = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJohansenLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLJohansenLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLJsFeatureRanking",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureRankingsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TopFeatureIndicesJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJsFeatureRanking", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLJsFeatureRanking_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLJumpTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaLevel = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JumpContributionToVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    JumpCount = table.Column<int>(type: "integer", nullable: false),
                    LocalBipowerVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MaxJumpSize = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    MeanJumpSize = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TotalVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLJumpTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLKalmanCoefficientLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KalmanGain = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PosteriorMean = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorVariance = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKalmanCoefficientLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLKalmanCoefficientLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLKalmanEmLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmIterations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeasurementNoise = table.Column<double>(type: "double precision", nullable: false),
                    NoiseRatio = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessNoise = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLKalmanEmLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLaplaceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibrationEce = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HessianApproximation = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LastLayerDim = table.Column<int>(type: "integer", nullable: false),
                    MarginalLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    NllImprovement = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PosteriorVarianceMean = table.Column<double>(type: "double precision", nullable: false),
                    PriorPrecision = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UseLinearizedLaplace = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLaplaceLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLaplaceLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLLassoGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CauseSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrangerPValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    LagOrder = table.Column<int>(type: "integer", nullable: false),
                    LassoCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLassoGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLassoVarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActiveLinks = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LassoLambda = table.Column<double>(type: "double precision", nullable: false),
                    NetSpilloverLasso = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SparsityRate = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLassoVarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLearnThenTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Alpha = table.Column<double>(type: "double precision", nullable: false),
                    CalibratedThreshold = table.Column<double>(type: "double precision", nullable: false),
                    CalibrationSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoverageGuarantee = table.Column<double>(type: "double precision", nullable: false),
                    EmpiricalRisk = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLearnThenTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLedoitWolfLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ShrinkageCoeff = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLedoitWolfLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLeverageCycleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CrashRiskElevated = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LeverageChange = table.Column<double>(type: "double precision", nullable: false),
                    LeverageIndex = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLeverageCycleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLevyProcessLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CalibrationLogLik = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExcessKurtosis = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VgNu = table.Column<double>(type: "double precision", nullable: false),
                    VgSigma = table.Column<double>(type: "double precision", nullable: false),
                    VgTheta = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLevyProcessLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLimeExplanationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LocalCoefficientsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    LocalIntercept = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    LocalR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionLogId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLimeExplanationLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLimeExplanationLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLLimeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InterceptValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KernelWidth = table.Column<double>(type: "double precision", nullable: false),
                    LocalFidelity = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerturbationCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    TopFeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TopFeatureWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLimeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLingamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CausalOrderJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConnectivityMatrixNorm = table.Column<double>(type: "double precision", nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxCausalStrength = table.Column<double>(type: "double precision", nullable: false),
                    MeanResidualNonGaussianity = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VariableCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLingamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLiquidityRegimeAlert",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IsAnomalous = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RollingMedianSpread = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    SpreadPercentileRank = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    SpreadPips = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TriggeredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UtcHour = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLiquidityRegimeAlert", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLotteryTicketLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AccuracyRetention = table.Column<double>(type: "double precision", nullable: false),
                    BaseAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsWinningTicket = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrunedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    PruningRound = table.Column<int>(type: "integer", nullable: false),
                    SparsityRatio = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLotteryTicketLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLLotteryTicketLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLLowRankLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccuracyRetention = table.Column<double>(type: "double precision", nullable: false),
                    CompressionRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LowRankAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    LowRankParamCount = table.Column<int>(type: "integer", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    OriginalParamCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLowRankLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLLVaRLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LiquidityAdjVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MarketImpactCost = table.Column<double>(type: "double precision", nullable: false),
                    MarketVaR95 = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpreadCost = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLLVaRLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMarchenkoPasturLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CleanedConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LambdaMax = table.Column<double>(type: "double precision", nullable: false),
                    LambdaMin = table.Column<double>(type: "double precision", nullable: false),
                    NoiseEigenvalueCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    SignalEigenvalueCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMarchenkoPasturLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMarkovSwitchGarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    RegimeVolatility = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransitionProb = table.Column<double>(type: "double precision", nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMarkovSwitchGarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMatrixProfileLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscordDistance = table.Column<double>(type: "double precision", nullable: false),
                    DiscordIndex = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MatrixProfileMean = table.Column<double>(type: "double precision", nullable: false),
                    MotifDistance = table.Column<double>(type: "double precision", nullable: false),
                    MotifIndexA = table.Column<int>(type: "integer", nullable: false),
                    MotifIndexB = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubsequenceLength = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMatrixProfileLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMatrixProfileLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMaxEntLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HighSurpriseSamples = table.Column<int>(type: "integer", nullable: false),
                    InformativeFeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LagrangeMultiplierNorm = table.Column<double>(type: "double precision", nullable: false),
                    MaxEntropyValue = table.Column<double>(type: "double precision", nullable: false),
                    MomentConstraintError = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurpriseThreshold = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMaxEntLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMaxEntLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMcdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DropoutRate = table.Column<double>(type: "double precision", nullable: false),
                    ForwardPasses = table.Column<int>(type: "integer", nullable: false),
                    HighUncertaintyCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionStd = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMcdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMcsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BootstrapReplications = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InConfidenceSet = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    McsPValue = table.Column<double>(type: "double precision", nullable: false),
                    ModelRank = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMcsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMdnParams",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MuJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    NumComponents = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PiJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    SigmaJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMdnParams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMdnParams_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMeanCvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConstraintBinding = table.Column<bool>(type: "boolean", nullable: false),
                    CvarAlpha = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OptimalCvar = table.Column<double>(type: "double precision", nullable: false),
                    OptimalReturn = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionSize = table.Column<double>(type: "double precision", nullable: false),
                    SolverIterations = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMeanCvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMembershipInferenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttackAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfidenceGap = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TestConfidenceMean = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrainConfidenceMean = table.Column<double>(type: "double precision", nullable: false),
                    VulnerabilityAlert = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMembershipInferenceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMfboLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BestHyperparamsJson = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    BestValidationAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FidelityUsed = table.Column<double>(type: "double precision", nullable: false),
                    HighFidelityEvals = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LowFidelityEvals = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurrogateCorrelation = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalCostSaved = table.Column<double>(type: "double precision", nullable: false),
                    TotalTrials = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMfboLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMfboLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMfdfaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HurstMax = table.Column<double>(type: "double precision", nullable: false),
                    HurstMean = table.Column<double>(type: "double precision", nullable: false),
                    HurstMin = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MultifractalWidth = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ScalingExponentQ2 = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMfdfaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMicropriceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Microprice = table.Column<double>(type: "double precision", nullable: false),
                    MicropriceBias = table.Column<double>(type: "double precision", nullable: false),
                    MidPrice = table.Column<double>(type: "double precision", nullable: false),
                    OrderImbalance = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMicropriceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMidasLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FitRSquared = table.Column<double>(type: "double precision", nullable: false),
                    HfPredictorType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LfTargetHorizon = table.Column<int>(type: "integer", nullable: false),
                    MidasBeta1 = table.Column<double>(type: "double precision", nullable: false),
                    MidasBeta2 = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMidasLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMineLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LinearMI = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MiGain = table.Column<double>(type: "double precision", nullable: false),
                    MineNetworkLoss = table.Column<double>(type: "double precision", nullable: false),
                    MutualInformation = table.Column<double>(type: "double precision", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMineLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMinTReconciliationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreReconciliationDisagreement = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    RawProbabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    ReconciledH1Probability = table.Column<double>(type: "double precision", precision: 10, scale: 8, nullable: false),
                    ReconciledProbabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TimeframeCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMinTReconciliationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLMiRedundancyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Feature1Index = table.Column<int>(type: "integer", nullable: false),
                    Feature2Index = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsRedundant = table.Column<bool>(type: "boolean", nullable: false),
                    MutualInformation = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMiRedundancyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMiRedundancyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLModelGoodnessOfFit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoxSnellR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    McFaddenR2 = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    NullLogLikelihood = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelGoodnessOfFit", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelGoodnessOfFit_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLModelMagnitudeStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationCoefficient = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanAbsoluteError = table.Column<double>(type: "double precision", nullable: false),
                    MeanSignedBias = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLModelMagnitudeStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLModelMagnitudeStats_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLMoeGatingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Expert0Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert1Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert2Activations = table.Column<int>(type: "integer", nullable: false),
                    Expert3Activations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMoeGatingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMoeGatingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMondrianCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    RegimeName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMondrianCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLMondrianCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLMsvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Regime = table.Column<int>(type: "integer", nullable: false),
                    RegimeProbability = table.Column<double>(type: "double precision", nullable: false),
                    RegimeSpillover = table.Column<double>(type: "double precision", nullable: false),
                    RegimeVarCoeff = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLMsvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNBeatsDecompLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FourierCoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrendAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    TrendSlope = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNBeatsDecompLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNcsnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FidProxy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LangevinSteps = table.Column<int>(type: "integer", nullable: false),
                    MaxNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    MinNoiseLevel = table.Column<double>(type: "double precision", nullable: false),
                    NoiseScaleLevels = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleDiversity = table.Column<double>(type: "double precision", nullable: false),
                    SamplesGenerated = table.Column<int>(type: "integer", nullable: false),
                    ScoreMatchingLoss = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNcsnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNeuralGrangerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CausingSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FullModelMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GrangerCausalEffect = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    MaxLagTested = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ReducedModelMse = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNeuralGrangerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNeuralProcessEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContextSize = table.Column<int>(type: "integer", nullable: false),
                    DecoderWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    EncoderWeightsJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNeuralProcessEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNflTestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmpiricalPValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MonteCarloRuns = table.Column<int>(type: "integer", nullable: false),
                    NullMeanAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    NullStdAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    ObservedAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNflTestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNmfLatentBasis",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BasisMatrixJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NumComponents = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNmfLatentBasis", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNmfLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergedIterations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    SparsityBasis = table.Column<double>(type: "double precision", nullable: false),
                    SparsityCoeff = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNmfLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNode2VecEmbeddingLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmbeddingJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MostSimilarSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NearestNeighborDistance = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WalkCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNode2VecEmbeddingLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNode2VecLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanCentrality = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TopFeature1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TopFeature2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TopFeature3 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNode2VecLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNowcastLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MacroVariable = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NowcastSurprise = table.Column<double>(type: "double precision", nullable: false),
                    NowcastValue = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionError = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNowcastLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLNsgaIILogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    BestEce = table.Column<double>(type: "double precision", nullable: false),
                    BestLatencyMs = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Generations = table.Column<int>(type: "integer", nullable: false),
                    HypervolumeDominated = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParetoFrontSize = table.Column<int>(type: "integer", nullable: false),
                    PopulationSize = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLNsgaIILogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLNsgaIILogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLOfiLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BullishPressure = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentOfi = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanOfi = table.Column<double>(type: "double precision", nullable: false),
                    OfiPercentileRank = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    StdOfi = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOfiLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOhlcVolatilityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CloseToCloseVol = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsensusVol = table.Column<double>(type: "double precision", nullable: false),
                    GarmanKlassVol = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParkinsonVol = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    YangZhangVol = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOhlcVolatilityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DualVariable = table.Column<double>(type: "double precision", nullable: false),
                    GradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LearningRate = table.Column<double>(type: "double precision", nullable: false),
                    MirrorMapType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioWeightsJson = table.Column<string>(type: "text", nullable: false),
                    RegretBound = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalSteps = table.Column<int>(type: "integer", nullable: false),
                    WeightEntropy = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOmdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOmegaCalmarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnnualisedReturn = table.Column<double>(type: "double precision", nullable: false),
                    CalmarRatio = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MaxDrawdown = table.Column<double>(type: "double precision", nullable: false),
                    OmegaRatio = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOmegaCalmarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLOosEquityCurveSnapshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AutoDemoted = table.Column<bool>(type: "boolean", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OosCalmar = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosMaxDrawdown = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosSharpe = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OosTotalReturn = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOosEquityCurveSnapshot", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOosEquityCurveSnapshot_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLOptimalStoppingLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmpiricalImprovementRate = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanConfidenceThreshold = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrialsStopped = table.Column<int>(type: "integer", nullable: false),
                    TrialsTotal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOptimalStoppingLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOptimalStoppingLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLOptimizationParetoFront",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLTrainingRunId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HypervolumeContrib = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeploymentCandidate = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: true),
                    ObjectiveAccuracy = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ObjectiveSharpe = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    ObjectiveStability = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParetoRank = table.Column<int>(type: "integer", nullable: false),
                    SearchBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOptimizationParetoFront", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOptimizationParetoFront_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MLOptimizationParetoFront_MLTrainingRun_MLTrainingRunId",
                        column: x => x.MLTrainingRunId,
                        principalTable: "MLTrainingRun",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLOuHalfLifeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AdfStatistic = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiffusionCoeff = table.Column<double>(type: "double precision", nullable: false),
                    HalfLifeDays = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LongRunMean = table.Column<double>(type: "double precision", nullable: false),
                    MeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    OuFitResidual = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLOuHalfLifeLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLOuHalfLifeLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLPackNetLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MaskCount = table.Column<int>(type: "integer", nullable: false),
                    MeanMaskedWeight = table.Column<double>(type: "double precision", nullable: false),
                    MeanUnmaskedWeight = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sparsity = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPackNetLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPartialDependenceBaseline",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BaselineComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BaselinePdpJson = table.Column<string>(type: "text", nullable: false),
                    CurrentPdpJson = table.Column<string>(type: "text", nullable: true),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GridValuesJson = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxDeviationFromBaseline = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPartialDependenceBaseline", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPartialDependenceBaseline_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLParticleFilterLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveSampleSize = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticleCount = table.Column<int>(type: "integer", nullable: false),
                    PosteriorMeanAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorRegime0 = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorRegime1 = table.Column<double>(type: "double precision", nullable: false),
                    PosteriorStd = table.Column<double>(type: "double precision", nullable: false),
                    ResamplingTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLParticleFilterLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPassiveAggressiveLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AggressiveCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanHingeLoss = table.Column<double>(type: "double precision", nullable: false),
                    MeanWeightNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PassiveCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdateCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPassiveAggressiveLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPathSignatureLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sig11 = table.Column<double>(type: "double precision", nullable: false),
                    Sig12 = table.Column<double>(type: "double precision", nullable: false),
                    Sig21 = table.Column<double>(type: "double precision", nullable: false),
                    Sig22 = table.Column<double>(type: "double precision", nullable: false),
                    SignatureNorm = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPathSignatureLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPcaWhiteningLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EigenvaluesJson = table.Column<string>(type: "text", nullable: false),
                    EigenvectorsJson = table.Column<string>(type: "text", nullable: false),
                    ExplainedVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcaWhiteningLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPcaWhiteningLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLPcCausalLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CausalDagJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MarkovBlanketFeaturesJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    MarkovBlanketSize = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcCausalLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPcmciLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CausalLag = table.Column<int>(type: "integer", nullable: false),
                    CauseSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificant = table.Column<bool>(type: "boolean", nullable: false),
                    MciPValue = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartialCorr = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPcmciLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPerformanceDecompositionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BiasComponent = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LuckComponent = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SkillComponent = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalAccuracy = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPerformanceDecompositionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPersistentLaplacianLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EdgeCount = table.Column<int>(type: "integer", nullable: false),
                    FiedlerValue0 = table.Column<double>(type: "double precision", nullable: false),
                    FiedlerValue1 = table.Column<double>(type: "double precision", nullable: false),
                    FilterThreshold = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanLaplacianEigenvalue = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpectralGap0 = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap1 = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TopologicalComplexity = table.Column<double>(type: "double precision", nullable: false),
                    VertexCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPersistentLaplacianLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLPredictionScorePsiLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentMeanProb = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    CurrentWeekCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSignificantShift = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PsiValue = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    ReferenceCount = table.Column<int>(type: "integer", nullable: false),
                    ReferenceMeanProb = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    WeekStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPredictionScorePsiLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLPredictionScorePsiLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLPriceImpactDecayLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImpactDecayHalfLife = table.Column<double>(type: "double precision", nullable: false),
                    InitialImpact = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersistentImpact = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLPriceImpactDecayLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLProfitFactorLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AverageLoss = table.Column<double>(type: "double precision", nullable: false),
                    AverageWin = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GrossLoss = table.Column<double>(type: "double precision", nullable: false),
                    GrossProfit = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LossCount = table.Column<int>(type: "integer", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProfitFactor = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    WinCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLProfitFactorLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQuadraticCovariationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CovariationPairSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CumulativePath = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuadraticCovariation = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuadraticCovariationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQuantileCoverageLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EmpiricalPicp = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanIntervalWidthPips = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    WindowEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WinklerScore = table.Column<double>(type: "double precision", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuantileCoverageLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLQuantileCoverageLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLQuantizationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BitsSimulated = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MaxAbsError = table.Column<double>(type: "double precision", nullable: false),
                    MeanAbsError = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantizationRange = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQuantizationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLQueryByCommitteeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommitteeSize = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DisagreementScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsHighlyInformative = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleIndex = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLQueryByCommitteeLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRademacherLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BoundSlack = table.Column<double>(type: "double precision", nullable: false),
                    Complexity = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneralizationBound = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    OverfittingRisk = table.Column<bool>(type: "boolean", nullable: false),
                    RandomTrials = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRademacherLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRademacherLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLRapsCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    EmpiricalCoverage = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KReg = table.Column<int>(type: "integer", nullable: false),
                    Lambda = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QHat = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRapsCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRapsCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRddLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BandwidthUsed = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CutoffValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LocalTreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ObservationsLeft = table.Column<int>(type: "integer", nullable: false),
                    ObservationsRight = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PValue = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ThresholdVariable = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRddLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedEigenvolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EigenConcentration = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LargestEigenvalue = table.Column<double>(type: "double precision", nullable: false),
                    MarketModeVol = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedEigenvolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedKernelLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsymptoticVariance = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KernelType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OptimalBandwidth = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedKernelVol = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedKernelLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealizedQuarticityLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuarticityRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RealizedQuarticity = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RvConfidenceIntervalHigh = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    RvConfidenceIntervalLow = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolOfVol = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealizedQuarticityLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRealNvpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AnomalyCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    StdLogLikelihood = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRealNvpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeCAPMLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlphaEstimate = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RSquared = table.Column<double>(type: "double precision", nullable: false),
                    Regime = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RegimeBeta = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeCAPMLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeFeatureImportance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ImportanceScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeFeatureImportance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRegimeFeatureImportance_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimePrototype",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrototypeFeaturesJson = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    RegimeName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimePrototype", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRegimePrototype_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRegimeSynchronyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystemicRisk = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrimarySymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Regime = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SynchronisedPairCount = table.Column<int>(type: "integer", nullable: false),
                    SynchronisedSymbolsJson = table.Column<string>(type: "text", nullable: false),
                    SynchronyScore = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRegimeSynchronyLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRenyiDivergenceLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    Alpha = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RenyiDivergence = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRenyiDivergenceLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRenyiDivergenceLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLReptileLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InnerSteps = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MetaLearningRate = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WeightDisplacementNorm = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLReptileLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLReservoirEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InputScaling = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadoutWeightsBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    ReconstructionError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    ReservoirSize = table.Column<int>(type: "integer", nullable: false),
                    SpectralRadius = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLReservoirEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRetrainingScheduleLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DriftScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformanceDelta = table.Column<double>(type: "double precision", nullable: false),
                    RetrainingTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TriggerReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRetrainingScheduleLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRetrogradeFalsePatternLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    FailureMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanFeaturesJson = table.Column<string>(type: "text", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TopDivergentDelta = table.Column<double>(type: "double precision", nullable: false),
                    TopDivergentFeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRetrogradeFalsePatternLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRetrogradeFalsePatternLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLRobustCovLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    OutlierFraction = table.Column<double>(type: "double precision", nullable: false),
                    RobustVariance = table.Column<double>(type: "double precision", nullable: false),
                    SampleVariance = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VarianceRatio = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRobustCovLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRollSpreadLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveBidAskBps = table.Column<double>(type: "double precision", nullable: false),
                    EstimatedSpread = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnAutocovariance = table.Column<double>(type: "double precision", nullable: false),
                    RollSpreadEstimate = table.Column<double>(type: "double precision", nullable: false),
                    SampleSize = table.Column<int>(type: "integer", nullable: false),
                    SerialCovariance = table.Column<double>(type: "double precision", nullable: false),
                    SpreadAlert = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    WindowSize = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRollSpreadLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRoroIndexLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsRiskOn = table.Column<bool>(type: "boolean", nullable: false),
                    MomentumComponent = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoroScore = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolComponent = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRoroIndexLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRotationForestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiversityMeasure = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NumTrees = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PcaComponents = table.Column<int>(type: "integer", nullable: false),
                    SubsetSize = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TestAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false),
                    TrainAccuracy = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRotationForestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRoughVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HurstExponent = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLogSlope = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoughnessIndex = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolForecast = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRoughVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLRpcaAnomalyLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AlertTriggered = table.Column<bool>(type: "boolean", nullable: false),
                    AnomalousSampleCount = table.Column<int>(type: "integer", nullable: false),
                    AnomalyThreshold = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MaxSparseNorm = table.Column<double>(type: "double precision", nullable: false),
                    MeanSparseNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLRpcaAnomalyLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLRpcaAnomalyLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSacLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntropyCoeff = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanQValue = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyEntropy = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UpdateSteps = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSacLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSageLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InteractionEffect = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MarginalContribution = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SageRank = table.Column<int>(type: "integer", nullable: false),
                    SageValue = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSamLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FlatnessMeasure = table.Column<double>(type: "double precision", nullable: false),
                    GeneralizationGap = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RhoPerturbation = table.Column<double>(type: "double precision", nullable: false),
                    SamLoss = table.Column<double>(type: "double precision", nullable: false),
                    SharpnessValue = table.Column<double>(type: "double precision", nullable: false),
                    StandardLoss = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSamLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSarimaNeuralLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HybridAccuracyGain = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NeuralAccuracyOnResiduals = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResidualMeanAbsError = table.Column<double>(type: "double precision", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    SarimaAic = table.Column<double>(type: "double precision", nullable: false),
                    SarimaPeriod = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSarimaNeuralLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSchrodingerBridgeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BridgeEntropy = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergedSuccessfully = table.Column<bool>(type: "boolean", nullable: false),
                    ForwardKl = table.Column<double>(type: "double precision", nullable: false),
                    IpfIterations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReverseKl = table.Column<double>(type: "double precision", nullable: false),
                    SourceSamples = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetSamples = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransportCost = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSchrodingerBridgeLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSchrodingerBridgeLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLScorecardLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CompositeGrade = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DrawdownScore = table.Column<double>(type: "double precision", nullable: false),
                    ExecutionScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MlAccuracyScore = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegimeAlignmentScore = table.Column<double>(type: "double precision", nullable: false),
                    SharpeScore = table.Column<double>(type: "double precision", nullable: false),
                    StrategyHealthScore = table.Column<double>(type: "double precision", nullable: false),
                    StrategyId = table.Column<long>(type: "bigint", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLScorecardLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSemiparametricVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Bandwidth = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KernelVolatility = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParametricVolatility = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSemiparametricVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSemivarianceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DownsideConcentration = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    DownsideSemivariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SemivarianceRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SignedVariation = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UpsideSemivariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSemivarianceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSessionPlattCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlattA = table.Column<double>(type: "double precision", nullable: false),
                    PlattB = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Session = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSessionPlattCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSessionPlattCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSgldLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanWeightVariance = table.Column<double>(type: "double precision", nullable: false),
                    NoiseScale = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PosteriorMeanShift = table.Column<double>(type: "double precision", nullable: false),
                    SamplingSteps = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSgldLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLShapDriftLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BaselineShap = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentShap = table.Column<double>(type: "double precision", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    FeatureName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativeMagnitudeShift = table.Column<double>(type: "double precision", nullable: false),
                    SignFlipped = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShapDriftLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShapDriftLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLShapInteractionLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FeatureA = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FeatureB = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InteractionScore = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLShapInteractionLog", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLShapInteractionLog_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSignatureTransformLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AugmentedDimensions = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanSignatureNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignatureDepth = table.Column<int>(type: "integer", nullable: false),
                    SignatureLength = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TopFeatureCorrelation = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSignatureTransformLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSinkhornLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MarginalError = table.Column<double>(type: "double precision", nullable: false),
                    OtDistance = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegularizationEps = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    SinkhornIterations = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TransportCost = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSinkhornLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSinkhornLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSkewRpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImpliedSkewness = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedSkewness = table.Column<double>(type: "double precision", nullable: false),
                    SkewnessRp = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSkewRpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSlicedWassersteinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NumProjections = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlicedWassersteinDistance = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSlicedWassersteinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSnapshotCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CycleIndex = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LrAtCapture = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    ValidationLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    WeightsBytes = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSnapshotCheckpoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSnapshotCheckpoint_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLSomModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GridCols = table.Column<int>(type: "integer", nullable: false),
                    GridRows = table.Column<int>(type: "integer", nullable: false),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantisationError = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    WeightsJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSomModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSparseEncoder",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EncoderBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    InputDim = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LatentDim = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionLoss = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    SparsityK = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSparseEncoder", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSparsePcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExplainedVarianceRatio = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanNonZeroLoadings = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    SparsityL1 = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSparsePcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpdNetLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BiMapLoss = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CovarianceRank = table.Column<int>(type: "integer", nullable: false),
                    FiedlerValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MatrixConditionNumber = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairCount = table.Column<int>(type: "integer", nullable: false),
                    RegimeSimilarity = table.Column<double>(type: "double precision", nullable: false),
                    ReigActivationMean = table.Column<double>(type: "double precision", nullable: false),
                    SpectralGap = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpdNetLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpectralGraphLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Cluster1Size = table.Column<int>(type: "integer", nullable: false),
                    Cluster2Size = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiedlerValue = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpectralGap = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpectralGraphLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSpilloverLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NetSpillover = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SymbolCount = table.Column<int>(type: "integer", nullable: false),
                    TotalSpillover = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSpilloverLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSplLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    AccuracyGain = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentPace = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanRejectedLoss = table.Column<double>(type: "double precision", nullable: false),
                    MeanSelectedLoss = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesSelected = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSplLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSplLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSprtLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogLikelihoodRatio = table.Column<double>(type: "double precision", nullable: false),
                    LowerBound = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SamplesConsumed = table.Column<int>(type: "integer", nullable: false),
                    StoppedHigh = table.Column<bool>(type: "boolean", nullable: false),
                    StoppedLow = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UpperBound = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSprtLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSsaComponentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NoiseVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    OscillatoryComponentCount = table.Column<int>(type: "integer", nullable: false),
                    OscillatoryVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SingularValuesJson = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TrendComponentCount = table.Column<int>(type: "integer", nullable: false),
                    TrendVarianceExplained = table.Column<double>(type: "double precision", nullable: false),
                    WindowLength = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSsaComponentLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSsaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComponentCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EigenvalueShareJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LagWindowSize = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalToNoiseRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrendReconstructionError = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSsaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStatArbLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CurrentZScore = table.Column<double>(type: "double precision", nullable: false),
                    EntryThreshold = table.Column<double>(type: "double precision", nullable: false),
                    HalfLifeDays = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalActive = table.Column<bool>(type: "boolean", nullable: false),
                    SpreadMean = table.Column<double>(type: "double precision", nullable: false),
                    SpreadStd = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStatArbLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLStatArbLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLStatArbPcaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OuLongRunMean = table.Column<double>(type: "double precision", nullable: false),
                    OuMeanReversionSpeed = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PcaResidual = table.Column<double>(type: "double precision", nullable: false),
                    SignalStrength = table.Column<double>(type: "double precision", nullable: false),
                    SpreadZScore = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStatArbPcaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStlDecompositionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CandleCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResidualVariance = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalAmplitude = table.Column<double>(type: "double precision", nullable: false),
                    SeasonalPeriod = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TrendMean = table.Column<double>(type: "double precision", nullable: false),
                    TrendSlope = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStlDecompositionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLStlDecompositionLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLStochVolLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LogVolMean = table.Column<double>(type: "double precision", nullable: false),
                    LogVolVariance = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersistenceParam = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStochVolLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLStructuredPruningLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MaxRowNorm = table.Column<double>(type: "double precision", nullable: false),
                    MinRowNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrunedNeurons = table.Column<int>(type: "integer", nullable: false),
                    PruningRatio = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TotalNeurons = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLStructuredPruningLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSuperstatisticsLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveDegreesOfFreedom = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GammaShapeK = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    GammaThetaScale = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsHeavyTailed = table.Column<bool>(type: "boolean", nullable: false),
                    LocalVarianceCoeffVar = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    StudentTFitKl = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    SuperstatsFitScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSuperstatisticsLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSurvivalLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaselineHazardMean = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConcordanceIndex = table.Column<double>(type: "double precision", nullable: false),
                    CoxCoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    HazardAtCurrentFeatures = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MedianSurvivalDays = table.Column<double>(type: "double precision", nullable: false),
                    MedianSurvivalTime = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Survival30Days = table.Column<double>(type: "double precision", nullable: false),
                    Survival60Days = table.Column<double>(type: "double precision", nullable: false),
                    Survival90Days = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSurvivalLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSurvivalModel",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaselineHazardJson = table.Column<string>(type: "text", nullable: false),
                    CoefficientsJson = table.Column<string>(type: "text", nullable: false),
                    ConcordanceIndex = table.Column<double>(type: "double precision", precision: 10, scale: 6, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSurvivalModel", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSvarLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MonetaryShock = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskShock = table.Column<double>(type: "double precision", nullable: false),
                    StructuralImpact = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSvarLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLSvgdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergedAtIteration = table.Column<int>(type: "integer", nullable: false),
                    EpistemicUncertainty = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    KernelBandwidth = table.Column<double>(type: "double precision", nullable: false),
                    MeanPrediction = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticleCount = table.Column<int>(type: "integer", nullable: false),
                    ParticleDiversity = table.Column<double>(type: "double precision", nullable: false),
                    PredictionVariance = table.Column<double>(type: "double precision", nullable: false),
                    SteinGradientNorm = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSvgdLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSvgdLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSymbolicRegressionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    BestExpressionJson = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    BestFitnessScore = table.Column<double>(type: "double precision", nullable: false),
                    BestR2Score = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DiscoveredOperators = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExpressionComplexity = table.Column<int>(type: "integer", nullable: false),
                    FeaturesUsedJson = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Generations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PopulationSize = table.Column<int>(type: "integer", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSymbolicRegressionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLSymbolicRegressionLogs_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MLSyntheticControlLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ControlWeightsJson = table.Column<string>(type: "text", nullable: false),
                    DonorCount = table.Column<int>(type: "integer", nullable: false),
                    EventDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventDescription = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostPeriodActual = table.Column<double>(type: "double precision", nullable: false),
                    PostPeriodSynthetic = table.Column<double>(type: "double precision", nullable: false),
                    PostTreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    PrePeriodFit = table.Column<double>(type: "double precision", nullable: false),
                    PreTreatmentFit = table.Column<double>(type: "double precision", nullable: false),
                    SignificantEffect = table.Column<bool>(type: "boolean", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TreatmentEffect = table.Column<double>(type: "double precision", nullable: false),
                    TreatmentSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLSyntheticControlLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTarchLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsymmetryGamma = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GarchAlpha = table.Column<double>(type: "double precision", nullable: false),
                    GarchBeta = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LeverageEffect = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VolatilityForecast = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTarchLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTaskArithmeticLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccuracyAfterAddition = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyAfterNegation = table.Column<double>(type: "double precision", nullable: false),
                    AccuracyBeforeArithmetic = table.Column<double>(type: "double precision", nullable: false),
                    BaseModelId = table.Column<long>(type: "bigint", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaskDescription = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TaskModelId = table.Column<long>(type: "bigint", nullable: false),
                    TaskVectorNorm = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTaskArithmeticLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTcavLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConceptName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LinearAccuracy = table.Column<double>(type: "double precision", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    NegativeSensitivity = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositiveSensitivity = table.Column<double>(type: "double precision", nullable: false),
                    StatisticalSignificance = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TcavScore = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<int>(type: "integer", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTcavLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTdaLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Betti0 = table.Column<int>(type: "integer", nullable: false),
                    Betti1 = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LongestBarcode = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersistenceEntropy = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TopologicalComplexity = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTdaLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTermStructVrpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TermSlope = table.Column<double>(type: "double precision", nullable: false),
                    Vrp1d = table.Column<double>(type: "double precision", nullable: false),
                    Vrp1h = table.Column<double>(type: "double precision", nullable: false),
                    Vrp4h = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTermStructVrpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTgcnLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GcnLayers = table.Column<int>(type: "integer", nullable: false),
                    GraphDensity = table.Column<double>(type: "double precision", nullable: false),
                    GruHiddenDim = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanEdgeWeight = table.Column<double>(type: "double precision", nullable: false),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    NodeEmbeddingNorm = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TemporalSmoothness = table.Column<double>(type: "double precision", nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TopNeighboursJson = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTgcnLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTransferEntropyLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Lag = table.Column<int>(type: "integer", nullable: false),
                    NetInformationFlow = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetSymbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TransferEntropyXtoY = table.Column<double>(type: "double precision", nullable: false),
                    TransferEntropyYtoX = table.Column<double>(type: "double precision", nullable: false),
                    XDrivesY = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTransferEntropyLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTsrvLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    NoiseVariance = table.Column<double>(type: "double precision", nullable: false),
                    OptimalScale = table.Column<int>(type: "integer", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RvFast = table.Column<double>(type: "double precision", nullable: false),
                    RvSlow = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TsrvEstimate = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTsrvLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTtaInferenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AugmentationCount = table.Column<int>(type: "integer", nullable: false),
                    AveragedProbability = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DirectionFlipped = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalProbability = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VarianceAcrossAugmentations = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTtaInferenceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLTuckerLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssetCount = table.Column<int>(type: "integer", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoreTensorNorm = table.Column<double>(type: "double precision", nullable: false),
                    FeatureCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RankAsset = table.Column<int>(type: "integer", nullable: false),
                    RankFeature = table.Column<int>(type: "integer", nullable: false),
                    RankTimeframe = table.Column<int>(type: "integer", nullable: false),
                    ReconstructionError = table.Column<double>(type: "double precision", nullable: false),
                    RelativeError = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TimeframeCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLTuckerLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLUniversalPortfolioLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BestCrpBenchmark = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CumulativeReturn = table.Column<double>(type: "double precision", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegretBound = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UniversalWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLUniversalPortfolioLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVennAbersCalibration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    CalibratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CalibrationSamples = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    IsotonicScores0Json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    IsotonicScores1Json = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    MeanIntervalWidth = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVennAbersCalibration", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MLVennAbersCalibration_MLModel_MLModelId",
                        column: x => x.MLModelId,
                        principalTable: "MLModel",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MLVmdLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConvergenceIterations = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ModeCenterFrequenciesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ModeCount = table.Column<int>(type: "integer", nullable: false),
                    ModeEnergiesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReconstructionError = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVmdLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVpinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BucketCount = table.Column<int>(type: "integer", nullable: false),
                    BuyImbalance = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ToxicityAlert = table.Column<bool>(type: "boolean", nullable: false),
                    Vpin = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVpinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLVrpLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ForecastBeta = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Horizon1ForecastR2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Horizon5ForecastR2 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealizedVariance = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VarianceRiskPremium = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    VrpZScore = table.Column<decimal>(type: "numeric(18,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLVrpLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWassersteinLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DriftDetected = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TestWindowDays = table.Column<int>(type: "integer", nullable: false),
                    ThresholdUsed = table.Column<double>(type: "double precision", nullable: false),
                    TrainWindowDays = table.Column<int>(type: "integer", nullable: false),
                    WassersteinDist = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWassersteinLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWaveletCoherenceLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CoherenceScale240 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CoherenceScale5 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    CoherenceScale60 = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DominantCoherenceScale = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MeanPhaseAngle = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    PhaseLockingRatio = table.Column<decimal>(type: "numeric(18,8)", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Symbol2 = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWaveletCoherenceLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWeibullTteLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    MLModelId = table.Column<long>(type: "bigint", nullable: false),
                    MeanRecoveryDays = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Percentile90RecoveryDays = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Timeframe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    WeibullScale = table.Column<double>(type: "double precision", nullable: false),
                    WeibullShape = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWeibullTteLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLWganCheckpoint",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscriminatorBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    DiscriminatorDim = table.Column<int>(type: "integer", nullable: false),
                    GeneratorBytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    GeneratorDim = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Timeframe = table.Column<int>(type: "integer", nullable: false),
                    TrainedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TrainingEpochs = table.Column<int>(type: "integer", nullable: false),
                    TrainingSamples = table.Column<int>(type: "integer", nullable: false),
                    WassersteinDistance = table.Column<double>(type: "double precision", precision: 18, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLWganCheckpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MLZumbachEffectLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AsymmetryScore = table.Column<double>(type: "double precision", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    LongScaleRv = table.Column<double>(type: "double precision", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShortScaleRv = table.Column<double>(type: "double precision", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ZumbachCoefficient = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MLZumbachEffectLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MLA2cLogs_MLModelId",
                table: "MLA2cLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAbsorptionRatioLogs_Symbol",
                table: "MLAbsorptionRatioLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLAcdDurationLogs_Symbol_ComputedAt",
                table: "MLAcdDurationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLActComplexityLogs_MLModelId",
                table: "MLActComplexityLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLActivationMaxLog_MLModelId",
                table: "MLActivationMaxLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveDropoutLogs_MLModelId",
                table: "MLAdaptiveDropoutLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdaptiveWindowConfig_Symbol_Timeframe",
                table: "MLAdaptiveWindowConfig",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdversarialRobustnessLog_MLModelId",
                table: "MLAdversarialRobustnessLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAdversarialValidationLog_Symbol_Timeframe_ComputedAt",
                table: "MLAdversarialValidationLog",
                columns: new[] { "Symbol", "Timeframe", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAdwinLogs_MLModelId",
                table: "MLAdwinLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAleLogs_MLModelId",
                table: "MLAleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLAlmgrenChrissLogs_Symbol_ComputedAt",
                table: "MLAlmgrenChrissLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAlphaStableLogs_Symbol_ComputedAt",
                table: "MLAlphaStableLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAmihudLogs_Symbol",
                table: "MLAmihudLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLArchLmTestLogs_Symbol_ComputedAt",
                table: "MLArchLmTestLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLAttentionWeightLog_MLModelId",
                table: "MLAttentionWeightLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBaldLogs_MLModelId",
                table: "MLBaldLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBarlowTwinsLogs_MLModelId",
                table: "MLBarlowTwinsLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBayesFactorLogs_MLModelId",
                table: "MLBayesFactorLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBayesThresholdLog_MLModelId",
                table: "MLBayesThresholdLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBetaVaeLogs_MLModelId",
                table: "MLBetaVaeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBipowerVariationLogs_Symbol_ComputedAt",
                table: "MLBipowerVariationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBivariateEvtLogs_Symbol_Symbol2_ComputedAt",
                table: "MLBivariateEvtLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLBlackLittermanLogs_MLModelId",
                table: "MLBlackLittermanLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBocpdChangePoint_MLModelId",
                table: "MLBocpdChangePoint",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBocpdLogs_MLModelId",
                table: "MLBocpdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBootstrapEnsemble_MLModelId",
                table: "MLBootstrapEnsemble",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLBvarLogs_Symbol_ComputedAt",
                table: "MLBvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLC51Distribution_MLModelId",
                table: "MLC51Distribution",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCandleBertEncoder_MLModelId",
                table: "MLCandleBertEncoder",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCandlestickLogs_Symbol",
                table: "MLCandlestickLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLCarrLogs_MLModelId_ComputedAt",
                table: "MLCarrLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCarryDecompLogs_MLModelId_ComputedAt",
                table: "MLCarryDecompLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCashSelectionLog_MLModelId",
                table: "MLCashSelectionLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalImpactLogs_ComputedAt",
                table: "MLCausalImpactLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLCausalImpactLogs_Symbol_Timeframe_EventDate",
                table: "MLCausalImpactLogs",
                columns: new[] { "Symbol", "Timeframe", "EventDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCipLogs_MLModelId_ComputedAt",
                table: "MLCipLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLClarkWestLogs_MLModelId1_MLModelId2_ComputedAt",
                table: "MLClarkWestLogs",
                columns: new[] { "MLModelId1", "MLModelId2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCmimFeatureRankingLog_MLModelId_SelectionRank",
                table: "MLCmimFeatureRankingLog",
                columns: new[] { "MLModelId", "SelectionRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCojumpLogs_Symbol_Symbol2_ComputedAt",
                table: "MLCojumpLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalAnomalyLog_MLModelId_DetectedAt",
                table: "MLConformalAnomalyLog",
                columns: new[] { "MLModelId", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalEfficiencyLog_MLModelId",
                table: "MLConformalEfficiencyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLConformalMartingaleLogs_MLModelId",
                table: "MLConformalMartingaleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLConsistencyModelLogs_MLModelId",
                table: "MLConsistencyModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLContrastiveEncoder_Symbol_Timeframe",
                table: "MLContrastiveEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCornishFisherLogs_MLModelId_ComputedAt",
                table: "MLCornishFisherLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCorrelationSurpriseLog_Symbol1_Symbol2_Timeframe",
                table: "MLCorrelationSurpriseLog",
                columns: new[] { "Symbol1", "Symbol2", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCorwinSchultzLogs_MLModelId_ComputedAt",
                table: "MLCorwinSchultzLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCounterfactualLogs_MLModelId",
                table: "MLCounterfactualLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCqrLogs_MLModelId",
                table: "MLCqrLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcCalibration_MLModelId",
                table: "MLCrcCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_ComputedAt",
                table: "MLCrcLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_MLModelId",
                table: "MLCrcLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrcLogs_Symbol",
                table: "MLCrcLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrossSecMomentumLogs_Symbol_ComputedAt",
                table: "MLCrossSecMomentumLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCrossStrategyTransferLogs_MLModelId",
                table: "MLCrossStrategyTransferLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrownLogs_MLModelId",
                table: "MLCrownLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLCrownLogs_Symbol_Timeframe",
                table: "MLCrownLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLCurrencyPairGraph_SourceSymbol_TargetSymbol_Timeframe",
                table: "MLCurrencyPairGraph",
                columns: new[] { "SourceSymbol", "TargetSymbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDataCartographyLogs_MLModelId",
                table: "MLDataCartographyLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDbscanClusterLogs_MLModelId",
                table: "MLDbscanClusterLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDccaLogs_Symbol_Symbol2_ComputedAt",
                table: "MLDccaLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDccGarchLogs_Symbol_Symbol2_ComputedAt",
                table: "MLDccGarchLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDesLogs_MLModelId",
                table: "MLDesLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDirichletUncertaintyLog_MLModelId",
                table: "MLDirichletUncertaintyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDklLogs_MLModelId",
                table: "MLDklLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDmlEffectLog_MLModelId",
                table: "MLDmlEffectLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDmTestLogs_ModelAId",
                table: "MLDmTestLogs",
                column: "ModelAId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDmTestLogs_ModelBId",
                table: "MLDmTestLogs",
                column: "ModelBId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDqnLogs_MLModelId",
                table: "MLDqnLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLDynamicFactorModelLogs_Symbol_ComputedAt",
                table: "MLDynamicFactorModelLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLDynotearsLogs_Symbol_ComputedAt",
                table: "MLDynotearsLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEbmLogs_MLModelId",
                table: "MLEbmLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEceLog_MLModelId",
                table: "MLEceLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEconomicImpactLog_EconomicEventId",
                table: "MLEconomicImpactLog",
                column: "EconomicEventId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEconomicImpactLog_MLModelId",
                table: "MLEconomicImpactLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEigenportfolioLogs_Symbol_ComputedAt",
                table: "MLEigenportfolioLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEmdDriftLog_MLModelId",
                table: "MLEmdDriftLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEnbPiLogs_MLModelId",
                table: "MLEnbPiLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEnsembleLearnerWeight_MLModelId_LearnerIndex",
                table: "MLEnsembleLearnerWeight",
                columns: new[] { "MLModelId", "LearnerIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLEntropyPoolingLogs_MLModelId_ComputedAt",
                table: "MLEntropyPoolingLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEstarLogs_MLModelId_ComputedAt",
                table: "MLEstarLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEtsParams_Symbol_Timeframe_FittedAt",
                table: "MLEtsParams",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEvalueLogs_MLModelId",
                table: "MLEvalueLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEventStudyLogs_MLModelId_ComputedAt",
                table: "MLEventStudyLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLEvidentialLogs_MLModelId",
                table: "MLEvidentialLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvidentialParams_MLModelId",
                table: "MLEvidentialParams",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvtGpdLogs_MLModelId",
                table: "MLEvtGpdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLEvtRiskEstimate_MLModelId",
                table: "MLEvtRiskEstimate",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp3Logs_Symbol",
                table: "MLExp3Logs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp4Logs_ComputedAt",
                table: "MLExp4Logs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLExp4Logs_Symbol_Timeframe",
                table: "MLExp4Logs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLExperienceReplayEntry_MLModelId_ResolvedAt",
                table: "MLExperienceReplayEntry",
                columns: new[] { "MLModelId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFactorCopulaLogs_MLModelId_ComputedAt",
                table: "MLFactorCopulaLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFactorModelLogs_MLModelId",
                table: "MLFactorModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFamaMacBethLogs_Symbol_ComputedAt",
                table: "MLFamaMacBethLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLFeatureNormStats_Symbol_Timeframe_Regime",
                table: "MLFeatureNormStats",
                columns: new[] { "Symbol", "Timeframe", "Regime" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLFederatedModelLogs_MLModelId",
                table: "MLFederatedModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFlowMatchingLogs_MLModelId",
                table: "MLFlowMatchingLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFpcaLogs_Symbol",
                table: "MLFpcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLFractionalDiffLogs_MLModelId",
                table: "MLFractionalDiffLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLFuzzyRegimeMembership_MLModelId",
                table: "MLFuzzyRegimeMembership",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchEvtCopulaLogs_MLModelId_ComputedAt",
                table: "MLGarchEvtCopulaLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchMLogs_Symbol_ComputedAt",
                table: "MLGarchMLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGarchModel_Symbol_Timeframe_FittedAt",
                table: "MLGarchModel",
                columns: new[] { "Symbol", "Timeframe", "FittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGasModelLogs_MLModelId",
                table: "MLGasModelLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_ComputedAt",
                table: "MLGaussianCopulaLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_MLModelId",
                table: "MLGaussianCopulaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGaussianCopulaLogs_Symbol",
                table: "MLGaussianCopulaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLGemLogs_MLModelId",
                table: "MLGemLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGjrGarchLogs_MLModelId",
                table: "MLGjrGarchLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGlobalSurrogateLogs_MLModelId",
                table: "MLGlobalSurrogateLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLGlostenMilgromLogs_Symbol_ComputedAt",
                table: "MLGlostenMilgromLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGonzaloGrangerLogs_MLModelId_ComputedAt",
                table: "MLGonzaloGrangerLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLGradientSaliencyLog_MLModelId",
                table: "MLGradientSaliencyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHamiltonRegimeLogs_MLModelId",
                table: "MLHamiltonRegimeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHansenSkewedTLogs_MLModelId_ComputedAt",
                table: "MLHansenSkewedTLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHarRvLogs_Symbol_ComputedAt",
                table: "MLHarRvLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHjbLogs_MLModelId",
                table: "MLHjbLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHoeffdingTreeLogs_MLModelId",
                table: "MLHoeffdingTreeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHotellingDriftLog_MLModelId",
                table: "MLHotellingDriftLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHrpAllocationLogs_MLModelId",
                table: "MLHrpAllocationLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHsicLingamLogs_ComputedAt",
                table: "MLHsicLingamLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLHsicLingamLogs_Symbol_Timeframe",
                table: "MLHsicLingamLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLHStatisticLog_MLModelId",
                table: "MLHStatisticLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHuberRegressionLogs_MLModelId",
                table: "MLHuberRegressionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLHyperbolicLogs_Symbol",
                table: "MLHyperbolicLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLHyperparamPrior_Symbol_Timeframe",
                table: "MLHyperparamPrior",
                columns: new[] { "Symbol", "Timeframe" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLIbEncoder_Symbol_Timeframe",
                table: "MLIbEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLIcaLogs_Symbol",
                table: "MLIcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLInformationShareLogs_MLModelId",
                table: "MLInformationShareLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLInputGradientNormLog_MLModelId",
                table: "MLInputGradientNormLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLInstrumentalVarLogs_MLModelId",
                table: "MLInstrumentalVarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIntegratedGradientsLog_MLModelId",
                table: "MLIntegratedGradientsLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLIsolationForestLogs_MLModelId",
                table: "MLIsolationForestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLJohansenLogs_MLModelId",
                table: "MLJohansenLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLJsFeatureRanking_MLModelId",
                table: "MLJsFeatureRanking",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLJumpTestLogs_Symbol_ComputedAt",
                table: "MLJumpTestLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLKalmanCoefficientLog_MLModelId_FeatureName",
                table: "MLKalmanCoefficientLog",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLKalmanEmLogs_Symbol_ComputedAt",
                table: "MLKalmanEmLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLaplaceLogs_MLModelId",
                table: "MLLaplaceLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLaplaceLogs_Symbol_Timeframe",
                table: "MLLaplaceLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLassoVarLogs_Symbol_ComputedAt",
                table: "MLLassoVarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLearnThenTestLogs_MLModelId",
                table: "MLLearnThenTestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLedoitWolfLogs_MLModelId",
                table: "MLLedoitWolfLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLeverageCycleLogs_Symbol_ComputedAt",
                table: "MLLeverageCycleLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLimeExplanationLog_MLModelId",
                table: "MLLimeExplanationLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLimeLogs_MLModelId",
                table: "MLLimeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLingamLogs_Symbol",
                table: "MLLingamLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLLiquidityRegimeAlert_Symbol_IsAnomalous_ResolvedAt",
                table: "MLLiquidityRegimeAlert",
                columns: new[] { "Symbol", "IsAnomalous", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLiquidityRegimeAlert_Symbol_TriggeredAt",
                table: "MLLiquidityRegimeAlert",
                columns: new[] { "Symbol", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLLotteryTicketLog_MLModelId",
                table: "MLLotteryTicketLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLowRankLogs_MLModelId",
                table: "MLLowRankLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLLVaRLogs_MLModelId_ComputedAt",
                table: "MLLVaRLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMarchenkoPasturLogs_Symbol",
                table: "MLMarchenkoPasturLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLMarkovSwitchGarchLogs_Symbol_ComputedAt",
                table: "MLMarkovSwitchGarchLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMatrixProfileLogs_MLModelId",
                table: "MLMatrixProfileLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMaxEntLogs_MLModelId",
                table: "MLMaxEntLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMcdLogs_MLModelId",
                table: "MLMcdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMcsLogs_MLModelId",
                table: "MLMcsLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMdnParams_MLModelId",
                table: "MLMdnParams",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMeanCvarLogs_MLModelId",
                table: "MLMeanCvarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMembershipInferenceLogs_MLModelId",
                table: "MLMembershipInferenceLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMfboLogs_MLModelId",
                table: "MLMfboLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMfdfaLogs_MLModelId",
                table: "MLMfdfaLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMicropriceLogs_Symbol_ComputedAt",
                table: "MLMicropriceLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMineLogs_MLModelId",
                table: "MLMineLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMinTReconciliationLog_Symbol_ComputedAt",
                table: "MLMinTReconciliationLog",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMiRedundancyLog_MLModelId",
                table: "MLMiRedundancyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelGoodnessOfFit_MLModelId",
                table: "MLModelGoodnessOfFit",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_MLModelId",
                table: "MLModelMagnitudeStats",
                column: "MLModelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLModelMagnitudeStats_Symbol_Timeframe",
                table: "MLModelMagnitudeStats",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLMoeGatingLog_MLModelId",
                table: "MLMoeGatingLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMondrianCalibration_MLModelId",
                table: "MLMondrianCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLMsvarLogs_Symbol_ComputedAt",
                table: "MLMsvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNBeatsDecompLogs_MLModelId",
                table: "MLNBeatsDecompLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNcsnLogs_ComputedAt",
                table: "MLNcsnLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLNcsnLogs_Symbol_Timeframe",
                table: "MLNcsnLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNeuralGrangerLogs_Symbol_CausingSymbol_ComputedAt",
                table: "MLNeuralGrangerLogs",
                columns: new[] { "Symbol", "CausingSymbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNeuralProcessEncoder_Symbol_Timeframe",
                table: "MLNeuralProcessEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNflTestLogs_MLModelId",
                table: "MLNflTestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNmfLatentBasis_Symbol_Timeframe",
                table: "MLNmfLatentBasis",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNmfLogs_Symbol",
                table: "MLNmfLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLNode2VecEmbeddingLogs_Symbol",
                table: "MLNode2VecEmbeddingLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLNode2VecLogs_MLModelId",
                table: "MLNode2VecLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLNowcastLogs_MLModelId_ComputedAt",
                table: "MLNowcastLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLNsgaIILogs_MLModelId",
                table: "MLNsgaIILogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOfiLogs_Symbol",
                table: "MLOfiLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLOhlcVolatilityLogs_MLModelId",
                table: "MLOhlcVolatilityLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmdLogs_ComputedAt",
                table: "MLOmdLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmdLogs_Symbol",
                table: "MLOmdLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLOmegaCalmarLogs_MLModelId",
                table: "MLOmegaCalmarLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOosEquityCurveSnapshot_MLModelId",
                table: "MLOosEquityCurveSnapshot",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimalStoppingLog_MLModelId",
                table: "MLOptimalStoppingLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_MLModelId",
                table: "MLOptimizationParetoFront",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_MLTrainingRunId",
                table: "MLOptimizationParetoFront",
                column: "MLTrainingRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_SearchBatchId_ParetoRank",
                table: "MLOptimizationParetoFront",
                columns: new[] { "SearchBatchId", "ParetoRank" });

            migrationBuilder.CreateIndex(
                name: "IX_MLOptimizationParetoFront_Symbol_Timeframe_IsDeploymentCand~",
                table: "MLOptimizationParetoFront",
                columns: new[] { "Symbol", "Timeframe", "IsDeploymentCandidate" });

            migrationBuilder.CreateIndex(
                name: "IX_MLOuHalfLifeLogs_MLModelId",
                table: "MLOuHalfLifeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPackNetLogs_MLModelId",
                table: "MLPackNetLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPartialDependenceBaseline_MLModelId_FeatureName",
                table: "MLPartialDependenceBaseline",
                columns: new[] { "MLModelId", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPartialDependenceBaseline_Symbol_Timeframe_FeatureName",
                table: "MLPartialDependenceBaseline",
                columns: new[] { "Symbol", "Timeframe", "FeatureName" });

            migrationBuilder.CreateIndex(
                name: "IX_MLParticleFilterLogs_MLModelId",
                table: "MLParticleFilterLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPassiveAggressiveLogs_MLModelId",
                table: "MLPassiveAggressiveLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPcaWhiteningLog_MLModelId",
                table: "MLPcaWhiteningLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPcCausalLog_Symbol_Timeframe",
                table: "MLPcCausalLog",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPerformanceDecompositionLogs_MLModelId",
                table: "MLPerformanceDecompositionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLPersistentLaplacianLogs_ComputedAt",
                table: "MLPersistentLaplacianLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLPersistentLaplacianLogs_Symbol",
                table: "MLPersistentLaplacianLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLPredictionScorePsiLog_MLModelId_WeekStartDate",
                table: "MLPredictionScorePsiLog",
                columns: new[] { "MLModelId", "WeekStartDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MLPredictionScorePsiLog_Symbol_IsSignificantShift",
                table: "MLPredictionScorePsiLog",
                columns: new[] { "Symbol", "IsSignificantShift" });

            migrationBuilder.CreateIndex(
                name: "IX_MLPriceImpactDecayLogs_Symbol_ComputedAt",
                table: "MLPriceImpactDecayLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLProfitFactorLogs_MLModelId",
                table: "MLProfitFactorLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLQuadraticCovariationLogs_Symbol_ComputedAt",
                table: "MLQuadraticCovariationLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantileCoverageLog_MLModelId_ComputedAt",
                table: "MLQuantileCoverageLog",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantileCoverageLog_Symbol_Timeframe_WindowEnd",
                table: "MLQuantileCoverageLog",
                columns: new[] { "Symbol", "Timeframe", "WindowEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_MLQuantizationLogs_MLModelId",
                table: "MLQuantizationLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLQueryByCommitteeLogs_MLModelId",
                table: "MLQueryByCommitteeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRademacherLogs_MLModelId",
                table: "MLRademacherLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRapsCalibration_MLModelId",
                table: "MLRapsCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRddLogs_MLModelId",
                table: "MLRddLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedEigenvolLogs_Symbol_ComputedAt",
                table: "MLRealizedEigenvolLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRealizedQuarticityLogs_Symbol_ComputedAt",
                table: "MLRealizedQuarticityLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRealNvpLogs_MLModelId",
                table: "MLRealNvpLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeCAPMLogs_MLModelId_ComputedAt",
                table: "MLRegimeCAPMLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeFeatureImportance_MLModelId_Regime",
                table: "MLRegimeFeatureImportance",
                columns: new[] { "MLModelId", "Regime" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimePrototype_MLModelId",
                table: "MLRegimePrototype",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRegimeSynchronyLog_PrimarySymbol_ComputedAt",
                table: "MLRegimeSynchronyLog",
                columns: new[] { "PrimarySymbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRenyiDivergenceLog_MLModelId",
                table: "MLRenyiDivergenceLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLReptileLogs_MLModelId",
                table: "MLReptileLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLReservoirEncoder_Symbol_Timeframe",
                table: "MLReservoirEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRetrainingScheduleLogs_MLModelId",
                table: "MLRetrainingScheduleLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRetrogradeFalsePatternLog_MLModelId_FailureMode",
                table: "MLRetrogradeFalsePatternLog",
                columns: new[] { "MLModelId", "FailureMode" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRobustCovLogs_MLModelId_ComputedAt",
                table: "MLRobustCovLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRollSpreadLogs_Symbol",
                table: "MLRollSpreadLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLRoroIndexLogs_Symbol_ComputedAt",
                table: "MLRoroIndexLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLRotationForestLogs_MLModelId",
                table: "MLRotationForestLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLRpcaAnomalyLog_MLModelId",
                table: "MLRpcaAnomalyLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSacLogs_MLModelId",
                table: "MLSacLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSageLogs_MLModelId",
                table: "MLSageLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSamLogs_MLModelId",
                table: "MLSamLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSarimaNeuralLogs_MLModelId",
                table: "MLSarimaNeuralLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSchrodingerBridgeLogs_MLModelId",
                table: "MLSchrodingerBridgeLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSchrodingerBridgeLogs_Symbol_Timeframe",
                table: "MLSchrodingerBridgeLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSemiparametricVolLogs_Symbol_ComputedAt",
                table: "MLSemiparametricVolLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSemivarianceLogs_Symbol_ComputedAt",
                table: "MLSemivarianceLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSessionPlattCalibration_MLModelId_Session",
                table: "MLSessionPlattCalibration",
                columns: new[] { "MLModelId", "Session" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSgldLogs_MLModelId",
                table: "MLSgldLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShapDriftLog_MLModelId",
                table: "MLShapDriftLog",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLShapInteractionLog_MLModelId_FeatureA_FeatureB",
                table: "MLShapInteractionLog",
                columns: new[] { "MLModelId", "FeatureA", "FeatureB" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSignatureTransformLogs_MLModelId",
                table: "MLSignatureTransformLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSinkhornLogs_MLModelId",
                table: "MLSinkhornLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSkewRpLogs_MLModelId_ComputedAt",
                table: "MLSkewRpLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSlicedWassersteinLogs_MLModelId",
                table: "MLSlicedWassersteinLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSnapshotCheckpoint_MLModelId_CycleIndex",
                table: "MLSnapshotCheckpoint",
                columns: new[] { "MLModelId", "CycleIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSomModel_Symbol_Timeframe",
                table: "MLSomModel",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSparseEncoder_Symbol_Timeframe",
                table: "MLSparseEncoder",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSparsePcaLogs_Symbol",
                table: "MLSparsePcaLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpdNetLogs_ComputedAt",
                table: "MLSpdNetLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpdNetLogs_Symbol",
                table: "MLSpdNetLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpectralGraphLogs_MLModelId",
                table: "MLSpectralGraphLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSpilloverLogs_MLModelId_ComputedAt",
                table: "MLSpilloverLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSplLogs_MLModelId",
                table: "MLSplLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSprtLogs_MLModelId",
                table: "MLSprtLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSsaComponentLogs_MLModelId",
                table: "MLSsaComponentLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSsaLogs_Symbol_ComputedAt",
                table: "MLSsaLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLStatArbLogs_MLModelId",
                table: "MLStatArbLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLStlDecompositionLogs_MLModelId",
                table: "MLStlDecompositionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLStochVolLogs_MLModelId_ComputedAt",
                table: "MLStochVolLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLStructuredPruningLogs_MLModelId",
                table: "MLStructuredPruningLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSuperstatisticsLogs_Symbol_ComputedAt",
                table: "MLSuperstatisticsLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSurvivalLogs_MLModelId",
                table: "MLSurvivalLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSurvivalModel_Symbol_Timeframe_IsActive",
                table: "MLSurvivalModel",
                columns: new[] { "Symbol", "Timeframe", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSvarLogs_Symbol_ComputedAt",
                table: "MLSvarLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_ComputedAt",
                table: "MLSvgdLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_MLModelId",
                table: "MLSvgdLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSvgdLogs_Symbol",
                table: "MLSvgdLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_ComputedAt",
                table: "MLSymbolicRegressionLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_MLModelId",
                table: "MLSymbolicRegressionLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLSymbolicRegressionLogs_Symbol",
                table: "MLSymbolicRegressionLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLSyntheticControlLogs_MLModelId",
                table: "MLSyntheticControlLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTarchLogs_MLModelId_ComputedAt",
                table: "MLTarchLogs",
                columns: new[] { "MLModelId", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTaskArithmeticLogs_BaseModelId",
                table: "MLTaskArithmeticLogs",
                column: "BaseModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTaskArithmeticLogs_TaskModelId",
                table: "MLTaskArithmeticLogs",
                column: "TaskModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTcavLogs_MLModelId",
                table: "MLTcavLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTermStructVrpLogs_Symbol_ComputedAt",
                table: "MLTermStructVrpLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTgcnLogs_ComputedAt",
                table: "MLTgcnLogs",
                column: "ComputedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MLTgcnLogs_Symbol_Timeframe",
                table: "MLTgcnLogs",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLTransferEntropyLogs_SourceSymbol",
                table: "MLTransferEntropyLogs",
                column: "SourceSymbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLTransferEntropyLogs_TargetSymbol",
                table: "MLTransferEntropyLogs",
                column: "TargetSymbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLTtaInferenceLogs_MLModelId",
                table: "MLTtaInferenceLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLTuckerLogs_Symbol",
                table: "MLTuckerLogs",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_MLVennAbersCalibration_MLModelId",
                table: "MLVennAbersCalibration",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLVmdLogs_Symbol_ComputedAt",
                table: "MLVmdLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLVpinLogs_MLModelId",
                table: "MLVpinLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLVrpLogs_Symbol_ComputedAt",
                table: "MLVrpLogs",
                columns: new[] { "Symbol", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLWaveletCoherenceLogs_Symbol_Symbol2_ComputedAt",
                table: "MLWaveletCoherenceLogs",
                columns: new[] { "Symbol", "Symbol2", "ComputedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MLWeibullTteLogs_MLModelId",
                table: "MLWeibullTteLogs",
                column: "MLModelId");

            migrationBuilder.CreateIndex(
                name: "IX_MLWganCheckpoint_Symbol_Timeframe",
                table: "MLWganCheckpoint",
                columns: new[] { "Symbol", "Timeframe" });

            migrationBuilder.CreateIndex(
                name: "IX_MLZumbachEffectLogs_Symbol_ComputedAt",
                table: "MLZumbachEffectLogs",
                columns: new[] { "Symbol", "ComputedAt" });
        }
    }
}
