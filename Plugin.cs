using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using Prometheus;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SMT.Stats;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("Supermarket Together.exe")]
public class Plugin : BaseUnityPlugin
{
    private const string SupermarketScene = "B_Main";
    private const string BoxesContainerName = "Boxes";
    private const string ShelvesContainerName = "Shelves";
    private const string StorageShelvesContainerName = "StorageShelves";
    internal static new ManualLogSource Logger;

    private Harmony _harmony = null!;


    private ConfigEntry<bool> configHandlerEnable;
    private ConfigEntry<int> configHandlerPort;

    private ConfigEntry<bool> configPushgatewayEnable;
    private ConfigEntry<string> configPushgatewayEndpoint;
    private ConfigEntry<string> configPushgatewayJob;


    private MetricHandler _metricsPusher = null;
    private MetricHandler _metricsHandler = null;

    private CollectorRegistry selfRegistry;


    internal static Gauge GameFunds;
    internal static Gauge SupermarketOpen;
    internal static Gauge GameFranchiseExp;
    internal static Gauge GameFranchisePoints;
    internal static Gauge ProductsTooExpensive;
    internal static Gauge ProductsNotFound;
    internal static Gauge ProductsCount;
    internal static Gauge ProductsPrice;
    internal static Histogram ProductsCheckoutPrice;

    private void Awake()
    {
        // Plugin startup logic
        Logger = base.Logger;

        configHandlerEnable = Config.Bind("handler", "enable", true, "Enable metrics handler");
        configHandlerPort = Config.Bind("handler", "port", 1234, "Metrics handler port");

        configPushgatewayEnable = Config.Bind("pushgateway", "enable", false, "Use Pushgateway");
        configPushgatewayEndpoint = Config.Bind("pushgateway", "endpoint", "http://localhost:9091/metrics", "Pushgateway endpoint");
        configPushgatewayJob = Config.Bind("pushgateway", "job", "SMT", "Pushgateway job");

        selfRegistry = Metrics.NewCustomRegistry();
        var selfFactory = Metrics.WithCustomRegistry(selfRegistry);

        GameFunds = selfFactory.CreateGauge("smt_funds", "Game funds");
        SupermarketOpen = selfFactory.CreateGauge("smt_supermarket_open", "Supermarket open");
        GameFranchiseExp = selfFactory.CreateGauge("smt_franchise_exp", "Franchise experience");
        GameFranchisePoints = selfFactory.CreateGauge("smt_franchise_points", "Franchise points");
        ProductsTooExpensive = selfFactory.CreateGauge("smt_products_too_expensive", "Number of product too expensive");
        ProductsNotFound = selfFactory.CreateGauge("smt_products_not_found", "Number of product not found");
        ProductsCount = selfFactory.CreateGauge("smt_products_count", "", labelNames: ["location", "product"]);
        ProductsPrice = selfFactory.CreateGauge("smt_products_price", "", labelNames: ["product"]);
        ProductsCheckoutPrice = selfFactory.CreateHistogram("smt_products_checkout_price", "", labelNames: ["product"]);

        _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), MyPluginInfo.PLUGIN_GUID);

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        selfRegistry.AddBeforeCollectCallback(async (cancel) =>
        {
            var gameDataManager = GameObject.Find("GameDataManager");
            if (gameDataManager == null)
            {
                Logger.LogWarning("GameDataManager not found !");
            }

            UpdateGeneralMetrics(gameDataManager);
            UpdateProductsCountMetric(gameDataManager);
            UpdateProductPrices(gameDataManager);
        });
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously

        StartMetricHandlers();

        configHandlerEnable.SettingChanged += OnConfigChange;
        configPushgatewayEnable.SettingChanged += OnConfigChange;

        SceneManager.sceneLoaded += OnSceneLoaded;

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
    }

    private void OnDestroy()
    {
        configHandlerEnable.SettingChanged -= OnConfigChange;
        configPushgatewayEnable.SettingChanged -= OnConfigChange;

        SceneManager.sceneLoaded -= OnSceneLoaded;

        _harmony.UnpatchSelf();
        StopMetricHandlers();

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is unloaded. Byebye !");
    }

    private void StartMetricHandlers()
    {
        StopMetricHandlers();

        if (configPushgatewayEnable.Value)
        {
            _metricsPusher = new MetricPusher(new MetricPusherOptions
            {
                Registry = selfRegistry,
                Endpoint = configPushgatewayEndpoint.Value,
                Job = configPushgatewayJob.Value,
            });
        }
        else
        {
            _metricsPusher = null;
        }

        if (configHandlerEnable.Value)
        {
            _metricsHandler = new MetricServer(
                port: configHandlerPort.Value,
                registry: selfRegistry
            );
        }
        else
        {
            _metricsHandler = null;
        }

        if (SceneManager.GetActiveScene().name == SupermarketScene)
        {
            Logger.LogInfo("Starting metric handlers");
            _metricsPusher?.Start();
            _metricsHandler?.Start();
        }
    }

    private void StopMetricHandlers()
    {
        _metricsPusher?.Stop();
        _metricsHandler?.Stop();
    }

    private void OnConfigChange(object sender, EventArgs e)
    {
        Logger.LogInfo($"Config changed !");
        StartMetricHandlers();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Logger.LogInfo($"Loading scene {scene.name}");
        if (scene.name == SupermarketScene)
        {
            Logger.LogInfo("In supermaket game !");
            StartMetricHandlers();
        }
        else
        {
            Logger.LogInfo("Left supermarket game");
            StopMetricHandlers();
        }
    }

    public static void UpdateGeneralMetrics(GameObject gameDataManager)
    {
        var gameData = gameDataManager.GetComponent<GameData>();

        GameFunds.Set(gameData.gameFunds);
        GameFranchiseExp.Set(gameData.gameFranchiseExperience);
        GameFranchisePoints.Set(gameData.gameFranchisePoints);
        // Logger.LogInfo($"productsTooExpensiveList: {productsTooExpensiveList.Value.Count}, productsNotFoundList: {productsNotFoundList.Value.Count}");

        SupermarketOpen.Set(gameData.isSupermarketOpen ? 1 : 0);

        // We obtain these values using patching so it can works on client
        // var traverseGameData = Traverse.Create(gameData);
        // var productsTooExpensiveList = traverseGameData.Property<List<int>>("productsTooExpensiveList");
        // var productsNotFoundList = traverseGameData.Property<List<int>>("productsNotFoundList");

        // ProductsNotFound.Set(productsNotFoundList.Value.Count);
        // ProductsTooExpensive.Set(productsTooExpensiveList.Value.Count);
    }

    public static void UpdateProductPrices(GameObject gameDataManager)
    {
        var productListing = gameDataManager.GetComponent<ProductListing>();

        foreach (var labelValues in ProductsPrice.GetAllLabelValues())
        {
            ProductsPrice.RemoveLabelled(labelValues);
        }

        foreach (var productID in productListing.availableProducts)
        {
            var productPrice = productListing.productPlayerPricing[productID];
            var productName = LocalizationManager.instance.GetLocalizationString("product" + productID);
            ProductsPrice.WithLabels([productName]).Set(productPrice);
        }
    }

    public static void UpdateProductsCountMetric(GameObject gameDataManager)
    {
        var managerBlackboard = gameDataManager.GetComponent<ManagerBlackboard>();
        // managerBlackboard.dummyArrayExistences[0] -> Shelves
        // managerBlackboard.dummyArrayExistences[1] -> StorageShelves
        // managerBlackboard.dummyArrayExistences[2] -> Boxes
        var dummyLength = managerBlackboard.dummyArrayExistences.Length;
        if (dummyLength != 3)
        {
            Logger.LogError($"Unepxected number of elements in dummyArrayExistences: got {dummyLength} expected 3");
            return;
        }

        // reset product listing
        foreach (var labelValues in ProductsCount.GetAllLabelValues())
        {
            // ProductsCount.RemoveLabelled(labelValues);
            ProductsCount.WithLabels(labelValues).Set(0);
        }

        for (int i = 0; i < dummyLength; i++)
        {
            var containerGameObject = managerBlackboard.dummyArrayExistences[i];
            // Logger.LogInfo($"Processing container type {containerGameObject.name}");
            if (containerGameObject.name != BoxesContainerName)
            {
                foreach (Transform item in containerGameObject.transform)
                {
                    // Logger.LogInfo($"Processing container element {item.name}");
                    int[] productInfoArray = item.GetComponent<Data_Container>().productInfoArray;
                    int num = productInfoArray.Length / 2;
                    for (int j = 0; j < num; j++)
                    {
                        var productID = productInfoArray[j * 2];
                        var productName = LocalizationManager.instance.GetLocalizationString("product" + productID);
                        var numberOfProducts = productInfoArray[j * 2 + 1];
                        if (numberOfProducts > 0)
                        {
                            ProductsCount.WithLabels([containerGameObject.name, productName]).Inc(numberOfProducts);
                            // Logger.LogInfo($"Found {numberOfProducts}x{productName} in {item.name}");
                        }
                    }
                }
            }
            else
            {
                foreach (Transform item in containerGameObject.transform)
                {
                    BoxData component = item.GetComponent<BoxData>();
                    var productID = component.productID;
                    var productName = LocalizationManager.instance.GetLocalizationString("product" + productID);
                    var numberOfProducts = component.numberOfProducts;
                    if (numberOfProducts > 0)
                    {
                        ProductsCount.WithLabels([containerGameObject.name, productName]).Inc(numberOfProducts);
                        // Logger.LogInfo($"Found {numberOfProducts}x{productName} in {item.name}");
                    }
                }
            }
        }

        // handle employees
        var employeeParentOBJ = NPC_Manager.Instance.employeeParentOBJ;
        var employeeCount = employeeParentOBJ.transform.childCount;
        for (int i = 0; i < employeeCount; i++)
        {
            var npcInfo = employeeParentOBJ.transform.GetChild(i).transform.GetComponent<NPC_Info>();
            int productID = npcInfo.boxProductID;
            var productName = LocalizationManager.instance.GetLocalizationString("product" + productID);
            int numberOfProducts = npcInfo.boxNumberOfProducts;
            if (numberOfProducts > 0)
            {
                // we'll say that NPC always hold items in a box
                ProductsCount.WithLabels([BoxesContainerName, productName]).Inc(numberOfProducts);
                // Logger.LogInfo($"Found NPC with {numberOfProducts}x{productName}");
            }
        }

        // handle players
        var networkManager = NetworkManager.singleton as CustomNetworkManager;
        foreach (PlayerObjectController gamePlayer in networkManager.GamePlayers)
        {
            var character = gamePlayer.GetComponent<PlayerSyncCharacter>();
            int productID = character.syncedProductID;
            var productName = LocalizationManager.instance.GetLocalizationString("product" + productID);
            int numberOfProducts = character.syncedNumberOfProducts;
            if (numberOfProducts > 0)
            {
                // we'll say that players always hold items in a box
                ProductsCount.WithLabels([BoxesContainerName, productName]).Inc(numberOfProducts);
                // Logger.LogInfo($"Found Player with {numberOfProducts}x{productName}");
            }
        }
    }

    [HarmonyPatch(typeof(GameData), "CmdOpenSupermarket")]
    public class GameData_CmdOpenSupermarket_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(GameData __instance)
        {
            Logger.LogInfo("Opening supermarket, clearing product counter");
            ProductsTooExpensive.Set(0);
            ProductsNotFound.Set(0);

            return true;
        }
    }

    [HarmonyPatch(typeof(GameData), "CmdEndDayFromButton")]
    public class GameData_CmdEndDayFromButton_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(GameData __instance)
        {
            Logger.LogInfo("End of the day, clearing product counter");
            ProductsTooExpensive.Set(0);
            ProductsNotFound.Set(0);

            return true;
        }
    }

    [HarmonyPatch(typeof(NPC_Info), "UserCode_RPCNotificationAboveHead__String__String")]
    public class NPC_Info_UserCode_RPCNotificationAboveHead_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(NPC_Info __instance, string message1, string messageAddon)
        {
            // Debug.Log($"Before RPC Notification. Message1: {message1}, MessageAddon: {messageAddon}");
            switch (message1)
            {
                case "NPCmessage0": // not found
                    ProductsNotFound.Inc();
                    break;
                case "NPCmessage1": // too expensive
                    ProductsTooExpensive.Inc();
                    break;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(ProductCheckoutSpawn), "CheckoutProductAnimation")]
    public class ProductCheckoutSpawn_CheckoutProductAnimation_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ProductCheckoutSpawn __instance)
        {
            var productName = LocalizationManager.instance.GetLocalizationString("product" + __instance.productID);

            // Debug.Log($"Checking out {productName} at {__instance.productCarryingPrice}");
            ProductsCheckoutPrice.WithLabels([productName]).Observe(__instance.productCarryingPrice);

            return true;
        }
    }
}
