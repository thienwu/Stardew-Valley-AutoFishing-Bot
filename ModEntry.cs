using ChibiKyu.StardewMods.Common;
using ChibiKyu.StardewMods.FishingAssistant2.Frameworks;
using FishingAssistant2;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ChibiKyu.StardewMods.FishingAssistant2
{
    public class ModEntry : Mod
    {
        public bool _shouldOpenSpotMenu = false;
        public bool CatchTreasure;
        public bool AutomationEnable;
        // private readonly string SecretKey = "FA_LITE_AUTH_KEY_XYZ_2026"; 
        private ModConfig Config { get; set; } = null!;
        private Assistant Assistant { get; set; } = null!;
        private ConfigMenu ConfigMenu { get; set; } = null!;

        public bool IsGridEditMode { get; set; } = false;
        public bool isGridVisible = false;
        private SButton toggleRouteRecordKey = SButton.F9;
        private SButton openRouteMenuKey = SButton.F10;    
        
        public bool isReturningToSleep = false; 
        public bool pendingReturnToSleep = false;
        public int LastF10Tab = 0;
        private int gridUpdateCounter = 0; 
        
        // Trạng thái giữ lực ném cần câu
        public static bool isBotHoldingCast = false;
        public static float botTargetCastPower = 1.0f;

        public static Texture2D PixelTexture = null!;
        private Dictionary<string, byte[,]> locationNavGrids = null!;
        private Dictionary<string, Dictionary<Point, byte>> tempEdits = new Dictionary<string, Dictionary<Point, byte>>();

        private Dictionary<string, MapGridSaveData> savedNavGrids = new Dictionary<string, MapGridSaveData>();
        private Dictionary<string, RouteSaveData> savedRoutes = new Dictionary<string, RouteSaveData>();
        public Dictionary<string, RouteSaveData> SavedRoutes => savedRoutes; 

        private bool isRecordingRoute = false;
        private List<Waypoint> recordingWaypoints = new List<Waypoint>();
        private Vector2 previousPlayerTile = new Vector2(-1, -1);
        
        private Queue<Waypoint> botRoute = new Queue<Waypoint>();
        private List<Point> currentMapPath = new List<Point>();   
        private int currentPathIndex = 0;
        
        private bool isBotRunning = false;
        private bool isWaitingForTransition = false;
        private string waitingFromMap = "";
        private int transitionWaitTimer = 0;
        private bool isBumpingEdge = false;
        private int pathfindingRetryCounter = 0;
        private int botStuckTimer = 0;
        private Vector2 botLastPos = Vector2.Zero;
        
        private static bool shouldBlockHalt = false;

        public override void Entry(IModHelper helper)
        {
            var harmony = new Harmony(this.ModManifest.UniqueID);
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Farmer), "updateMovementAnimation"),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_updateMovementAnimation)),
                postfix: new HarmonyMethod(typeof(ModEntry), nameof(Postfix_updateMovementAnimation))
            );
            
            // Patch logic ném mồi từ từ
            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Tools.FishingRod), nameof(StardewValley.Tools.FishingRod.tickUpdate)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_FishingRod_tickUpdate))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(StardewValley.Farmer), nameof(StardewValley.Farmer.Halt)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(Prefix_Halt))
            );

            I18n.Init(helper.Translation);
            Config = helper.ReadConfig<ModConfig>();
            locationNavGrids = new Dictionary<string, byte[,]>();

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.Saving += OnSaving;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.TimeChanged += OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            
            helper.Events.Player.Warped += OnWarped;
            helper.Events.Player.InventoryChanged += OnInventoryChanged;
            
            helper.Events.Display.RenderingHud += OnRenderingHud;
            helper.Events.Display.RenderedWorld += OnRenderedWorld;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            
            helper.Events.Input.ButtonPressed += OnButtonPressed;
            
            helper.Events.World.ObjectListChanged += OnObjectListChanged;
            helper.Events.World.TerrainFeatureListChanged += OnTerrainFeatureListChanged;
            Assistant = new Assistant(() => this, () => Config);
        }

        public void ForceDisable() 
        { 
            Game1.playSound("coin");
            AutomationEnable = false; 
            isReturningToSleep = false; 
            isBotRunning = false;
            shouldBlockHalt = false;
            CommonHelper.PushToggle(AutomationEnable, I18n.HudMessage_AutomationToggle());
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            ConfigMenu = new ConfigMenu(Helper.ModRegistry, ModManifest, () => Config, () => { Config = new ModConfig(); Helper.WriteConfig(Config); }, () => { Helper.WriteConfig(Config); Config = Helper.ReadConfig<ModConfig>(); ConfigMenu.OnConfigSavedCallback?.Invoke(); }, Assistant.OnConfigSaved);
            ConfigMenu.RegisterModConfigMenu();
            PixelTexture = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            PixelTexture.SetData(new[] { Color.White });
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            Assistant.GiveStarterFishingRod(Config.StartWithFishingRod);
            LoadGridData();

            // if (!Context.IsMainPlayer) 
            //     this.Helper.Multiplayer.SendMessage(message: this.SecretKey, messageType: "FishingAssistantAuth", modIDs: new[] { "FunnySnek.AntiCheatServer" }, playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID });
        }

        private void OnSaving(object? sender, SavingEventArgs e) { SaveAllGridData(); }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            Assistant.NumWarnThisDay = 0;
            isReturningToSleep = false; 

            if (AutomationEnable && savedRoutes.ContainsKey("AutoFishing"))
            {
                RunRoute("AutoFishing");
                Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.new-day"), 1));
            }
        }

        private void OnTimeChanged(object? sender, TimeChangedEventArgs e) 
        {
            Assistant.DoOnTimeChangedAssistantTask();
            if (Config.AutoPauseFishing == "WarnAndPause" && Game1.timeOfDay == Config.TimeToPause * 100)
            {
                if (savedRoutes.ContainsKey("AutoFishing"))
                {
                    pendingReturnToSleep = true;
                }
            }
        }

        private void OnWarped(object? sender, WarpedEventArgs e) 
        { 
            Assistant.ActualTrashJunk();
            if (isRecordingRoute) { recordingWaypoints.Add(new Waypoint(e.NewLocation.Name)); Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.record-map", new { mapName = e.NewLocation.Name }), 2)); }
        }
        
        private void OnInventoryChanged(object? sender, InventoryChangedEventArgs e) => Assistant.AutoTrashJunk(e);

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            if (e.NewMenu is BobberBar bobberBar) Assistant.OnFishingMiniGameStart(bobberBar);
            else if (e.OldMenu is BobberBar) Assistant.OnFishingMiniGameEnd();
            if (e.NewMenu is ItemGrabMenu { source: ItemGrabMenu.source_fishingChest } itemGrabMenu) Assistant.OnTreasureMenuOpen(itemGrabMenu);
            else if (e.OldMenu is ItemGrabMenu { source: ItemGrabMenu.source_fishingChest }) Assistant.OnTreasureMenuClose();
            if (e.NewMenu is GameMenu gameMenu) Assistant.OnGameMenuOpen(gameMenu);
            else if (e.OldMenu is GameMenu) Assistant.OnGameMenuClose();
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            bool isMovementKey = e.Button == SButton.W || e.Button == SButton.A || e.Button == SButton.S || e.Button == SButton.D ||
                                 e.Button == SButton.Up || e.Button == SButton.Down || e.Button == SButton.Left || e.Button == SButton.Right ||
                                 e.Button == SButton.DPadUp ||
                                 e.Button == SButton.DPadDown || e.Button == SButton.DPadLeft || e.Button == SButton.DPadRight ||
                                 e.Button == SButton.LeftThumbstickUp || e.Button == SButton.LeftThumbstickDown ||
                                 e.Button == SButton.LeftThumbstickLeft || e.Button == SButton.LeftThumbstickRight;

            if (isMovementKey)
            {
                bool wasCanceled = false;
                if (isBotRunning)
                {
                    isBotRunning = false;
                    shouldBlockHalt = false;
                    botRoute.Clear();
                    currentMapPath.Clear();
                    isReturningToSleep = false;
                    Game1.player.Halt();
                    wasCanceled = true;
                }
                if (wasCanceled) Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.cancel-autowalk"), 3));
            }

            if (e.Button == Config.EnableAutomationButton) 
            { 
                AutomationEnable = !AutomationEnable;
                Game1.playSound("coin"); 
                Assistant.OnAutomationStateChange(AutomationEnable); 
                
                if (!AutomationEnable) 
                {
                    isReturningToSleep = false;
                    isBotRunning = false;
                }
            }
            if (e.Button == Config.CatchTreasureButton) { CatchTreasure = !CatchTreasure;
                Game1.playSound("dwop"); }
            if (e.Button == Config.OpenConfigMenuButton && Config.OpenConfigMenuButton != SButton.None) ConfigMenu.OpenModMenu();
            if (e.Button == openRouteMenuKey && !isRecordingRoute) Game1.activeClickableMenu = new RouteDashboardMenu(this, false);
            if (e.Button == toggleRouteRecordKey)
            {
                if (!isRecordingRoute) { isRecordingRoute = true;
                    recordingWaypoints.Clear(); recordingWaypoints.Add(new Waypoint(Game1.currentLocation.Name, new Point((int)Game1.player.Tile.X, (int)Game1.player.Tile.Y))); Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.recording"), 2)); }
                else { isRecordingRoute = false;
                    Point targetTile = new Point((int)Game1.player.Tile.X, (int)Game1.player.Tile.Y); if (recordingWaypoints.Count > 0) recordingWaypoints[recordingWaypoints.Count - 1].SpecificTile = targetTile; Game1.activeClickableMenu = new RouteDashboardMenu(this, true);
                }
            }

            if (e.Button == SButton.MouseLeft && IsGridEditMode && isGridVisible && Game1.activeClickableMenu == null)
            {
                Vector2 cursorTile = Helper.Input.GetCursorPosition().Tile;
                int x = (int)cursorTile.X, y = (int)cursorTile.Y;
                GameLocation? location = Game1.currentLocation;
                if (location != null && location.Map != null && x >= 0 && x < location.Map.Layers[0].LayerWidth && y >= 0 && y < location.Map.Layers[0].LayerHeight)
                {
                    string mapName = location.Name;
                    if (!tempEdits.ContainsKey(mapName)) tempEdits[mapName] = new Dictionary<Point, byte>();
                    
                    Point p = new Point(x, y);
                    byte currentColor = 0;
                    if (tempEdits[mapName].ContainsKey(p)) 
                        currentColor = tempEdits[mapName][p];
                    else if (locationNavGrids.ContainsKey(mapName) && x < locationNavGrids[mapName].GetLength(0) && y < locationNavGrids[mapName].GetLength(1))
                        currentColor = locationNavGrids[mapName][x, y];
                    else
                        currentColor = (byte)(IsTileReallyPassable(location, x, y) ? 0 : 1);
                    tempEdits[mapName][p] = (byte)(currentColor == 1 ? 0 : 1);
                    
                    Game1.playSound("stoneStep");
                    UpdateCurrentMapGridRealTime();
                    Helper.Input.Suppress(e.Button);
                }
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            
            if (pendingReturnToSleep)
            {
                bool isFishingBusy = false;
                if (Game1.activeClickableMenu is StardewValley.Menus.BobberBar || Game1.activeClickableMenu is StardewValley.Menus.ItemGrabMenu)
                    isFishingBusy = true;
                
                if (Game1.player.CurrentTool is StardewValley.Tools.FishingRod rod)
                {
                    if (rod.isReeling || rod.pullingOutOfWater || rod.fishCaught || rod.showingTreasure || rod.recordSize || Game1.player.freezePause > 0)
                        isFishingBusy = true;
                }

                if (!isFishingBusy)
                {
                    pendingReturnToSleep = false;
                    isReturningToSleep = true;
                    if (Game1.player.CurrentTool is StardewValley.Tools.FishingRod currentRod)
                    {
                        Game1.player.completelyStopAnimatingOrDoingAction();
                        currentRod.doneFishing(Game1.player, true);
                        isBotHoldingCast = false;
                    }
                    AutomationEnable = true;
                    RunRouteReverse("AutoFishing");
                    Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.time-up"), 1));
                }
            }

            if (Game1.player.CurrentTool is FishingRod fishingRod) Assistant.OnEquipFishingRod(fishingRod, AutomationEnable);
            if (Game1.player.CurrentTool is not FishingRod) Assistant.OnUnEquipFishingRod();
            Assistant.DoOnUpdateAssistantTask();

            gridUpdateCounter++;
            if (gridUpdateCounter >= 10) 
            {
                gridUpdateCounter = 0;
                if (isGridVisible || isBotRunning) UpdateCurrentMapGridRealTime();
            }

            Vector2 currentPlayerTile = Game1.player.Tile;
            if (Game1.currentLocation != null && currentPlayerTile != previousPlayerTile)
            {
                // [FIX LỖI LƯU RÁC]: Đã xóa đoạn code tự động ghi đè số 0 (Xanh) vào tempEdits khi di chuyển

                foreach (var f in Game1.currentLocation.furniture)
                {
                    if (f is StardewValley.Objects.BedFurniture || f.furniture_type.Value == 14 || f.furniture_type.Value == 15 || f.Name.IndexOf("Bed", StringComparison.OrdinalIgnoreCase) >= 0) 
                    {
                        int minX = (int)f.TileLocation.X;
                        int maxX = minX + (f.GetBoundingBox().Width / Game1.tileSize) - 1;
                        int targetY = (int)f.TileLocation.Y + 1;
                        if ((int)currentPlayerTile.X >= minX && (int)currentPlayerTile.X <= maxX && (int)currentPlayerTile.Y == targetY)
                        {
                            if (!Game1.eventUp && isReturningToSleep) 
                            {
                                isReturningToSleep = false;
                                isBotRunning = false;
                                shouldBlockHalt = false;
                                Game1.player.Halt();
                                Game1.player.faceDirection(Game1.player.FacingDirection);
                                if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox dbx) { dbx.closeDialogue(); } Game1.activeClickableMenu = null;
                                Game1.currentLocation.lastQuestionKey = "Sleep";
                                Game1.currentLocation.answerDialogueAction("Sleep_Yes", new string[0]);
                            }
                        }
                    }
                }
            }
            previousPlayerTile = currentPlayerTile;
            
            if (isBotRunning && botRoute.Count > 0)
            {
                if (Game1.player.isInBed.Value) Game1.player.isInBed.Value = false;
                if (Game1.eventUp || Game1.fadeToBlack || Game1.isWarping || Game1.player.freezePause > 0 || !Game1.player.CanMove)
                {
                    shouldBlockHalt = false;
                    if (Game1.player.movementDirections.Count > 0 && !isWaitingForTransition)
                    {
                        Game1.player.Halt();
                    }
                    return;
                }

                if (isWaitingForTransition)
                {
                    if (Game1.activeClickableMenu != null && Game1.currentLocation != null)
                    {
                        string target = botRoute.Peek().MapName;
                        if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
                        {
                            string actionKey = "";
                            if (target == "Mine") actionKey = "Minecart_Mine";
                            else if (target == "Town") actionKey = "Minecart_Town";
                            else if (target == "Mountain") actionKey = "Minecart_Mountain";
                            else if (target == "BusStop" && Game1.currentLocation.Name == "Desert") actionKey = "DesertBus_Yes"; 
                            else if (target == "BusStop") actionKey = "Minecart_Bus";
                            else if (target == "Desert") actionKey = "Bus_Yes";
                            
                            if (string.IsNullOrEmpty(actionKey) && db.responses != null)
                            {
                                foreach (var r in db.responses)
                                {
                                    if (r.responseKey.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0 || 
                                        r.responseKey.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                        r.responseKey.IndexOf("Boat", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        actionKey = r.responseKey;
                                        break;
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(actionKey))
                            {
                                Game1.currentLocation.answerDialogueAction(actionKey, null);
                                db.closeDialogue();
                                Game1.activeClickableMenu = null;
                            }
                        }
                        else if (Game1.activeClickableMenu is StardewValley.Menus.MineElevatorMenu)
                        {
                            if (target.StartsWith("UndergroundMine"))
                            {
                                string floorStr = target.Replace("UndergroundMine", "");
                                if (int.TryParse(floorStr, out int floor))
                                {
                                    Game1.enterMine(floor);
                                    Game1.playSound("crystal");
                                    Game1.activeClickableMenu = null;
                                }
                            }
                        }
                    }

                    transitionWaitTimer++;
                    if (Game1.currentLocation != null && Game1.currentLocation.Name != waitingFromMap) { 
                        isWaitingForTransition = false; isBumpingEdge = false; transitionWaitTimer = 0; shouldBlockHalt = false; Game1.player.Halt();
                        pathfindingRetryCounter = 0; botStuckTimer = 0;
                    }
                    else if (transitionWaitTimer > 180) { Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.err-timeout"), 3));
                        isBotRunning = false; isWaitingForTransition = false; isBumpingEdge = false; transitionWaitTimer = 0; shouldBlockHalt = false; Game1.player.Halt();
                    }
                    else if (isBumpingEdge) 
                    {
                        if (botRoute.Count > 0 && Game1.currentLocation != null)
                        {
                            string destMap = botRoute.Peek().MapName;
                            foreach (var b in Game1.currentLocation.buildings)
                            {
                                if (b.indoors.Value != null && b.indoors.Value.Name == destMap) { Game1.player.FacingDirection = 0;
                                    break; }
                                if (b.buildingType.Value != null && b.buildingType.Value.Contains("Farmhouse", StringComparison.OrdinalIgnoreCase) && destMap.StartsWith("FarmHouse")) { Game1.player.FacingDirection = 0;
                                    break; }
                            }
                        }
                        float speed = Game1.player.speed + (Game1.player.addedSpeed > 0 ? Game1.player.addedSpeed : 0);
                        if (Game1.player.FacingDirection == 0) Game1.player.Position += new Vector2(0, -speed);
                        else if (Game1.player.FacingDirection == 1) Game1.player.Position += new Vector2(speed, 0);
                        else if (Game1.player.FacingDirection == 2) Game1.player.Position += new Vector2(0, speed);
                        else if (Game1.player.FacingDirection == 3) Game1.player.Position += new Vector2(-speed, 0);
                        shouldBlockHalt = true; 
                    }
                    else { Game1.player.Halt();
                    }
                    return;
                }

                GameLocation? currentLocation = Game1.currentLocation;
                if (currentLocation == null) return;
                
                Waypoint nextStop = botRoute.Peek();
                Point targetTile = Point.Zero;
                if (currentMapPath.Count == 0)
                {
                    Point playerTile = new Point((int)currentPlayerTile.X, (int)currentPlayerTile.Y);
                    if (currentLocation.Name == nextStop.MapName)
                    {
                        if (nextStop.SpecificTile.HasValue) targetTile = nextStop.SpecificTile.Value;
                        else 
                        { 
                            botRoute.Dequeue();
                            if (botRoute.Count == 0) 
                            { 
                                isBotRunning = false;
                                shouldBlockHalt = false;
                                Game1.player.Halt(); 
                                
                                if (currentLocation.Name.StartsWith("FarmHouse") || currentLocation.Name.StartsWith("Cabin"))
                                {
                                    // AutomationEnable remains intact so the bot restarts tomorrow
                                    Assistant.OnAutomationStateChange(false);
                                    
                                    List<Point>? bedSequence = FindBedSequence(currentLocation);
                                    if (bedSequence != null && bedSequence.Count > 0)
                                    {
                                        Point lastTile = bedSequence[bedSequence.Count - 1];
                                        
                                        if (Vector2.Distance(Game1.player.Tile, new Vector2(lastTile.X, lastTile.Y)) > 1.2f)
                                        {
                                            foreach (Point p in bedSequence) botRoute.Enqueue(new Waypoint(currentLocation.Name, p));
                                            isBotRunning = true;
                                        }
                                        else
                                        {
                                            if (isReturningToSleep)
                                            {
                                                isReturningToSleep = false;
                                                isBotRunning = false;
                                                shouldBlockHalt = false;
                                                Game1.player.Halt();
                                                Game1.player.faceDirection(Game1.player.FacingDirection);
                                                if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox dbx) { dbx.closeDialogue(); } Game1.activeClickableMenu = null;
                                                currentLocation.lastQuestionKey = "Sleep";
                                                currentLocation.answerDialogueAction("Sleep_Yes", new string[0]);
                                            }
                                        }
                                    }
                                }
                                else if (!isReturningToSleep)
                                {
                                    AutoFaceWater(currentLocation, Game1.player.Tile);
                                    AutomationEnable = true;
                                    Assistant.OnAutomationStateChange(true);
                                    Game1.playSound("coin");
                                    CommonHelper.PushToggle(AutomationEnable, I18n.HudMessage_AutomationToggle());
                                }
                            } 
                            return;
                        }
                    }
                    else
                    {
                        Point? warpPoint = FindTransitionTile(currentLocation, nextStop.MapName);
                        if (warpPoint.HasValue) targetTile = warpPoint.Value;
                        else { Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.err-door", new { mapName = nextStop.MapName }), 3));
                            isBotRunning = false; shouldBlockHalt = false; Game1.player.Halt(); return; }
                    }

                    if (pathfindingRetryCounter > 0)
                    {
                        pathfindingRetryCounter++;
                        if (pathfindingRetryCounter > 180) { Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.err-stuck"), 3)); isBotRunning = false; shouldBlockHalt = false; Game1.player.Halt(); return;
                        }
                        if (pathfindingRetryCounter % 10 != 0) return;
                    }

                    if (!locationNavGrids.ContainsKey(currentLocation.Name)) 
                    {
                        UpdateCurrentMapGridRealTime();
                        if (!locationNavGrids.ContainsKey(currentLocation.Name))
                        {
                            Game1.addHUDMessage(new HUDMessage("Lỗi hệ thống: Không thể tải lưới điều hướng cho map này!", 3));
                            isBotRunning = false; shouldBlockHalt = false; Game1.player.Halt(); return;
                        }
                    }

                    currentMapPath = AStarSolver.FindPath(locationNavGrids[currentLocation.Name], currentLocation, playerTile, targetTile);
                    currentPathIndex = 0;

                    if (currentMapPath.Count == 0 && playerTile != targetTile) { if (pathfindingRetryCounter == 0) pathfindingRetryCounter = 1; return;
                    }
                    else pathfindingRetryCounter = 0;
                }

                if (currentPathIndex < currentMapPath.Count)
                {
                    Point targetNode = currentMapPath[currentPathIndex];
                    float targetX = (targetNode.X * Game1.tileSize) + (Game1.tileSize / 2f) - (Game1.player.GetBoundingBox().Width / 2f);
                    float targetY = (targetNode.Y * Game1.tileSize) + (Game1.tileSize / 2f) - (Game1.player.GetBoundingBox().Height / 2f);
                    Vector2 targetPixelPos = new Vector2(targetX, targetY);
                    if (Vector2.Distance(Game1.player.Position, targetPixelPos) < 5f) { currentPathIndex++; botStuckTimer = 0;
                    }
                    else
                    {
                        if (Vector2.Distance(Game1.player.Position, botLastPos) < 1.0f) { botStuckTimer++;
                            if (botStuckTimer > 30) { currentMapPath.Clear(); botStuckTimer = 0; return; } } else botStuckTimer = 0;
                        botLastPos = Game1.player.Position;
                        Vector2 dir = targetPixelPos - Game1.player.Position; dir.Normalize();
                        float speed = Game1.player.speed + (Game1.player.addedSpeed > 0 ? Game1.player.addedSpeed : 0);
                        Game1.player.Position += dir * speed;
                        if (Math.Abs(dir.X) > Math.Abs(dir.Y)) Game1.player.FacingDirection = dir.X > 0 ? 1 : 3;
                        else Game1.player.FacingDirection = dir.Y > 0 ? 2 : 0;
                        shouldBlockHalt = true;
                    }
                }
                else
                {
                    if (currentLocation.Name != nextStop.MapName)
                    {
                        Point lastTileOfPath = currentMapPath.Count > 0 ?
                            currentMapPath[currentMapPath.Count - 1] : targetTile;
                        bool triggered = false;
                        foreach (Warp w in currentLocation.warps) if (w.X == lastTileOfPath.X && w.Y == lastTileOfPath.Y) { Game1.warpFarmer(w.TargetName, w.TargetX, w.TargetY, Game1.player.FacingDirection);
                            triggered = true; break; }
                        if (!triggered) foreach (var door in currentLocation.doors.Pairs) if (door.Key.X == lastTileOfPath.X && door.Key.Y == lastTileOfPath.Y) { currentLocation.checkAction(new xTile.Dimensions.Location(lastTileOfPath.X, lastTileOfPath.Y), Game1.viewport, Game1.player);
                            triggered = true; break; }
                        if (!triggered) foreach (var building in currentLocation.buildings) if (building.getPointForHumanDoor() == lastTileOfPath || building.occupiesTile(new Vector2(lastTileOfPath.X, lastTileOfPath.Y))) { building.doAction(new Vector2(lastTileOfPath.X, lastTileOfPath.Y), Game1.player);
                            triggered = true; break; }

                        if (!triggered)
                        {
                            float speed = Game1.player.speed + (Game1.player.addedSpeed > 0 ? Game1.player.addedSpeed : 0);
                            if (Game1.player.FacingDirection == 0) Game1.player.Position += new Vector2(0, -speed);
                            else if (Game1.player.FacingDirection == 1) Game1.player.Position += new Vector2(speed, 0);
                            else if (Game1.player.FacingDirection == 2) Game1.player.Position += new Vector2(0, speed);
                            else if (Game1.player.FacingDirection == 3) Game1.player.Position += new Vector2(-speed, 0);
                            
                            shouldBlockHalt = true;
                        }
                        
                        if (botStuckTimer > 60) { currentMapPath.Clear(); botStuckTimer = 0; } else botStuckTimer++;
                        isWaitingForTransition = true;
                        shouldBlockHalt = false;
                        waitingFromMap = currentLocation.Name; transitionWaitTimer = 0; 
                        
                        bool isEdge = lastTileOfPath.X <= 0 || lastTileOfPath.Y <= 0 || lastTileOfPath.X >= currentLocation.Map.Layers[0].LayerWidth - 1 || lastTileOfPath.Y >= currentLocation.Map.Layers[0].LayerHeight - 1;
                        isBumpingEdge = !triggered && isEdge; 
                        
                        if (triggered) Game1.player.Halt();
                        currentMapPath.Clear();
                    }
                    else
                    {
                        botRoute.Dequeue();
                        currentMapPath.Clear();
                        if (botRoute.Count == 0) 
                        { 
                            isBotRunning = false;
                            shouldBlockHalt = false;
                            Game1.player.Halt(); 
                            
                            if (currentLocation.Name.StartsWith("FarmHouse") || currentLocation.Name.StartsWith("Cabin"))
                            {
                                // AutomationEnable remains intact so the bot restarts tomorrow
                                Assistant.OnAutomationStateChange(false);
                                
                                List<Point>? bedSequence = FindBedSequence(currentLocation);
                                if (bedSequence != null && bedSequence.Count > 0)
                                {
                                    Point lastTile = bedSequence[bedSequence.Count - 1];
                                    
                                    if (Vector2.Distance(Game1.player.Tile, new Vector2(lastTile.X, lastTile.Y)) > 1.2f)
                                    {
                                        foreach (Point p in bedSequence) botRoute.Enqueue(new Waypoint(currentLocation.Name, p));
                                        isBotRunning = true;
                                    }
                                    else
                                    {
                                        if (isReturningToSleep)
                                        {
                                            isReturningToSleep = false;
                                            isBotRunning = false;
                                            shouldBlockHalt = false;
                                            Game1.player.Halt();
                                            Game1.player.faceDirection(Game1.player.FacingDirection);
                                            if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox dbx) { dbx.closeDialogue(); } Game1.activeClickableMenu = null;
                                            currentLocation.lastQuestionKey = "Sleep";
                                            currentLocation.answerDialogueAction("Sleep_Yes", new string[0]);
                                        }
                                    }
                                }
                            }
                            else if (!isReturningToSleep)
                            {
                                AutoFaceWater(currentLocation, Game1.player.Tile);
                                AutomationEnable = true;
                                Assistant.OnAutomationStateChange(true);
                                Game1.playSound("coin");
                                CommonHelper.PushToggle(AutomationEnable, I18n.HudMessage_AutomationToggle());
                            }
                        }
                    }
                }
            }
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            if (isGridVisible)
            {
                Vector2 playerDrawPos = new Vector2(Game1.player.Tile.X * Game1.tileSize, Game1.player.Tile.Y * Game1.tileSize) - new Vector2(Game1.viewport.X, Game1.viewport.Y);
                Color highlightBg = new Color(0, 191, 255, 30); Color highlightBorder = new Color(0, 191, 255, 160);
                e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)playerDrawPos.X, (int)playerDrawPos.Y, Game1.tileSize, Game1.tileSize), highlightBg);
                int border = 2; 
                e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)playerDrawPos.X, (int)playerDrawPos.Y, Game1.tileSize, border), highlightBorder);
                e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)playerDrawPos.X, (int)playerDrawPos.Y + Game1.tileSize - border, Game1.tileSize, border), highlightBorder); 
                e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)playerDrawPos.X, (int)playerDrawPos.Y, border, Game1.tileSize), highlightBorder);
                e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)playerDrawPos.X + Game1.tileSize - border, (int)playerDrawPos.Y, border, Game1.tileSize), highlightBorder);
            }
            bool isStuck = isBotRunning && !isWaitingForTransition && (pathfindingRetryCounter > 0 || botStuckTimer > 0);
            if ((isGridVisible || isStuck) && Game1.currentLocation != null && locationNavGrids.ContainsKey(Game1.currentLocation.Name))
            {
                byte[,] grid = locationNavGrids[Game1.currentLocation.Name];
                for (int x = Math.Max(0, Game1.viewport.X / Game1.tileSize); x < Math.Min(grid.GetLength(0), (Game1.viewport.X + Game1.viewport.Width) / Game1.tileSize + 1); x++)
                {
                    for (int y = Math.Max(0, Game1.viewport.Y / Game1.tileSize); y < Math.Min(grid.GetLength(1), (Game1.viewport.Y + Game1.viewport.Height) / Game1.tileSize + 1); y++)
                    {
                        if (!isGridVisible && isStuck && grid[x, y] != 1) continue;
                        Color overlay = grid[x, y] == 1 ? new Color(220, 20, 60, 45) : new Color(50, 205, 50, 20);
                        Vector2 pos = new Vector2(x * Game1.tileSize, y * Game1.tileSize) - new Vector2(Game1.viewport.X, Game1.viewport.Y);
                        e.SpriteBatch.Draw(PixelTexture, new Rectangle((int)pos.X, (int)pos.Y, Game1.tileSize, Game1.tileSize), overlay);
                    }
                }
            }
        }

        private void OnRenderingHud(object? sender, RenderingHudEventArgs e)
        {
            DrawModStatus();
            if (!isGridVisible || !Context.IsWorldReady) return;
            Vector2 mousePos = Helper.Input.GetCursorPosition().GetScaledScreenPixels(), cursorTile = Helper.Input.GetCursorPosition().Tile;
            string info = Helper.Translation.Get("navtool.hud.coords", new { x = cursorTile.X, y = cursorTile.Y });
            e.SpriteBatch.DrawString(Game1.dialogueFont, info, new Vector2(mousePos.X + 24, mousePos.Y + 24), Color.Yellow);
        }

        private void DrawModStatus()
        {
            if ((Game1.eventUp && !Game1.isFestival()) || (!AutomationEnable && !CatchTreasure)) return;
            float toolBarTransparency = 0; int toolBarWidth = 0;
            foreach (IClickableMenu? menu in Game1.onScreenMenus) { if (menu is not Toolbar toolBar) continue;
                toolBarTransparency = Helper.Reflection.GetField<float>(toolBar, "transparency").GetValue(); toolBarWidth = toolBar.width / 2; break;
            }
            Viewport viewport = Game1.graphics.GraphicsDevice.Viewport; Point playerGlobalPos = Game1.player.GetBoundingBox().Center;
            Vector2 playerLocalVec = Game1.GlobalToLocal(Game1.viewport, new Vector2(playerGlobalPos.X, playerGlobalPos.Y));
            bool alignTop = !Game1.options.pinToolbarToggle && playerLocalVec.Y > viewport.Height / 2 + 64;
            int toolbarOffset = toolBarTransparency == 0 || Assistant.IsInFishingMiniGame || Game1.isFestival() ? 0 : Config.ModStatusPosition == HudPosition.Left.ToString() ?
                -toolBarWidth - 2 : toolBarWidth + 2;
            int boxPosX = viewport.Width / 2 + toolbarOffset - 48;
            int boxPosY = alignTop ? 8 : viewport.Height - 8 - 96;
            Rectangle[] rectangles = { new(0, 256, 60, 60), new(20, 428, 10, 10), new(137, 412, 10, 11) };
            IClickableMenu.drawTextureBox(Game1.spriteBatch, Game1.menuTexture, rectangles[0], boxPosX, boxPosY, 96, 96, Color.White * toolBarTransparency, drawShadow: false);
            DrawIcon(AutomationEnable, rectangles[1], boxPosX + 48 - 10, boxPosY + 24, 2f);
            DrawIcon(CatchTreasure, rectangles[2], boxPosX + 48 - 10, boxPosY + 96 - 24 - 20, 2f);
            void DrawIcon(bool value, Rectangle source, int x, int y, float scale) { float iconTransparency = value ? 1 : 0.2f;
                ClickableTextureComponent icon = new(new Rectangle(x, y, 20, 20), Game1.mouseCursors, source, scale); icon.draw(Game1.spriteBatch, Color.White * toolBarTransparency * iconTransparency, 0);
            }
        }

        public void LoadGridData()
        {
            savedNavGrids = Helper.Data.ReadJsonFile<Dictionary<string, MapGridSaveData>>("NavGridData.json") ??
                new Dictionary<string, MapGridSaveData>();
            
            var loadedRoutes = Helper.Data.ReadJsonFile<Dictionary<string, RouteSaveData>>("NavRoutesData.json");
            if (loadedRoutes != null && loadedRoutes.Count > 0)
            {
                savedRoutes = loadedRoutes;
            }
            else if (System.IO.File.Exists(System.IO.Path.Combine(Helper.DirectoryPath, "NavRoutesData.json")))
            {
                var oldRoutes = Helper.Data.ReadJsonFile<Dictionary<string, List<Waypoint>>>("NavRoutesData.json");
                savedRoutes = new Dictionary<string, RouteSaveData>();
                if (oldRoutes != null)
                {
                    foreach (var kvp in oldRoutes)
                    {
                        savedRoutes[kvp.Key] = new RouteSaveData { FarmName = Game1.player?.farmName.Value ?? "Unknown", CharacterName = Game1.player?.Name ?? "Unknown", Waypoints = kvp.Value };
                    }
                }
            }
            else
            {
                savedRoutes = new Dictionary<string, RouteSaveData>();
            }
            
            tempEdits.Clear();
            UpdateCurrentMapGridRealTime();
        }

        public void ClearCurrentMapGrid()
        {
            if (Game1.currentLocation != null)
            {
                string mapName = Game1.currentLocation.Name;
                tempEdits.Remove(mapName);
                if (savedNavGrids.ContainsKey(mapName)) savedNavGrids.Remove(mapName);
                locationNavGrids.Remove(mapName);
                SaveAllGridData();
                Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.cleared-map", new { mapName = mapName }), 2));
                UpdateCurrentMapGridRealTime();
            }
        }

        public void SaveAllGridData()
        {
            foreach (var mapData in tempEdits)
            {
                string mapName = mapData.Key;
                if (!savedNavGrids.ContainsKey(mapName)) savedNavGrids[mapName] = new MapGridSaveData();
                var data = savedNavGrids[mapName];
                foreach (var kvp in mapData.Value)
                {
                    Vector2 vec = new Vector2(kvp.Key.X, kvp.Key.Y);
                    data.Obstacles.Remove(vec); data.Walkables.Remove(vec);
                    if (kvp.Value == 1) data.Obstacles.Add(vec); else if (kvp.Value == 0) data.Walkables.Add(vec);
                }
            }
            tempEdits.Clear();
            Helper.Data.WriteJsonFile("NavGridData.json", savedNavGrids);
            Helper.Data.WriteJsonFile("NavRoutesData.json", savedRoutes); 
        }

        public void RunRoute(string name) 
        { 
            if (!savedRoutes.ContainsKey(name)) return;
            botRoute.Clear(); 
            List<Waypoint> original = savedRoutes[name].Waypoints; 
            string curMap = Game1.currentLocation.Name; 
            int startIndex = -1;
            for (int i = original.Count - 1; i >= 0; i--) 
            { 
                if (original[i].MapName == curMap) { startIndex = i;
                    break; } 
            } 
            
            if (startIndex == -1) 
            { 
                if (curMap.StartsWith("FarmHouse") || curMap.StartsWith("Cabin"))
                {
                    for (int i = 0; i < original.Count; i++) 
                    { 
                        botRoute.Enqueue(new Waypoint(original[i].MapName, original[i].SpecificTile));
                    } 
                }
                else
                {
                    Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.err-wrong-map"), 3));
                    return; 
                }
            } 
            else if (startIndex == original.Count - 1) 
            { 
                botRoute.Enqueue(new Waypoint(original[startIndex].MapName, original[startIndex].SpecificTile));
            } 
            else 
            { 
                for (int i = startIndex + 1; i < original.Count; i++) 
                { 
                    botRoute.Enqueue(new Waypoint(original[i].MapName, original[i].SpecificTile));
                } 
            } 
            
            currentMapPath.Clear();
            isBotRunning = true; 
            isWaitingForTransition = false; 
            waitingFromMap = ""; 
            isBumpingEdge = false; 
            pathfindingRetryCounter = 0;
            botStuckTimer = 0;
            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.run-route", new { routeName = name }), 1));
        }
        
        public void RunRouteReverse(string name) 
        { 
            if (!savedRoutes.ContainsKey(name)) return;
            botRoute.Clear();
            
            List<Waypoint> original = savedRoutes[name].Waypoints; 
            List<Waypoint> revList = new List<Waypoint>();
            for (int i = original.Count - 1; i >= 0; i--) 
            { 
                Waypoint w = new Waypoint(original[i].MapName);
                if (i == 0) w.SpecificTile = original[0].SpecificTile; 
                revList.Add(w); 
            } 
            
            string curMap = Game1.currentLocation.Name;
            int startIndex = -1;
            
            for (int i = revList.Count - 1; i >= 0; i--) 
            { 
                if (revList[i].MapName == curMap) { startIndex = i;
                    break; } 
            } 
            
            if (startIndex == -1) 
            { 
                Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.err-wrong-map"), 3));
                return; 
            } 
            
            if (startIndex == revList.Count - 1) 
            { 
                botRoute.Enqueue(revList[startIndex]);
            } 
            else 
            { 
                for (int i = startIndex + 1; i < revList.Count; i++) 
                { 
                    botRoute.Enqueue(revList[i]);
                } 
            } 
            
            string homeMap = Game1.player.homeLocation.Value;
            if (!string.IsNullOrEmpty(homeMap) && revList.Count > 0)
            {
                if (!revList[revList.Count - 1].MapName.StartsWith("FarmHouse", StringComparison.OrdinalIgnoreCase) && 
                    !revList[revList.Count - 1].MapName.StartsWith("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    botRoute.Enqueue(new Waypoint(homeMap));
                }
            }
            
            currentMapPath.Clear();
            isBotRunning = true; 
            isWaitingForTransition = false; 
            waitingFromMap = ""; 
            isBumpingEdge = false; 
            pathfindingRetryCounter = 0;
            botStuckTimer = 0;
            // AutomationEnable remains intact so the bot restarts tomorrow
            Assistant.OnAutomationStateChange(false);
            CommonHelper.PushToggle(AutomationEnable, I18n.HudMessage_AutomationToggle());

            Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.run-rev-route", new { routeName = name }), 1));
        }
        
        public void DeleteRoute(string name) { if (savedRoutes.Remove(name)) SaveAllGridData(); }
        public void RenameRoute(string oldName, string newName) { 
            if (savedRoutes.ContainsKey(oldName) && !string.IsNullOrWhiteSpace(newName)) { 
                if (savedRoutes.ContainsKey(newName)) { Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.name-exists").Default("Tên hành trình đã tồn tại!").ToString(), 3)); return; }
                savedRoutes[newName] = savedRoutes[oldName];
                savedRoutes.Remove(oldName); SaveAllGridData(); Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.renamed"), 1)); 
            } 
        }
        public void SaveRecordedRoute(string name) { 
            if (!string.IsNullOrWhiteSpace(name)) { 
                if (savedRoutes.ContainsKey(name)) { Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.name-exists").Default("Tên hành trình đã tồn tại!").ToString(), 3)); return; }
                savedRoutes[name] = new RouteSaveData { FarmName = Game1.player?.farmName.Value ?? "Unknown", CharacterName = Game1.player?.Name ?? "Unknown", Waypoints = new List<Waypoint>(recordingWaypoints) };
                SaveAllGridData(); Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.saved-route", new { routeName = name }), 1));
            } 
        }
        public void CancelRecording() { recordingWaypoints.Clear(); Game1.addHUDMessage(new HUDMessage(Helper.Translation.Get("navtool.msg.cancel-record"), 3)); }

        private void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e) 
        { 
            if (e.Added.Any() || e.Removed.Any())
            {
                UpdateCurrentMapGridRealTime(); 
            }
        }

        private void OnTerrainFeatureListChanged(object? sender, TerrainFeatureListChangedEventArgs e) 
        { 
            if (e.Added.Any() || e.Removed.Any())
            {
                UpdateCurrentMapGridRealTime(); 
            }
        }
        
        private void UpdateCurrentMapGridRealTime() { GameLocation?
            loc = Game1.currentLocation; if (loc == null || loc.Map == null) return; string mapName = loc.Name; int 
w = loc.Map.Layers[0].LayerWidth;
            int h = loc.Map.Layers[0].LayerHeight; byte[,]? grid; if (!locationNavGrids.TryGetValue(mapName, out 
grid) || grid.GetLength(0) != w || grid.GetLength(1) != h) { grid = new byte[w, h];
                locationNavGrids[mapName] = grid; } 
            for (int x = 0; x < w; x++) { for (int y = 0; y < h; y++) { grid[x, y] = 
(byte)(IsTileReallyPassable(loc, x, y) ? 0 : 1);
                } } 
            if (savedNavGrids.TryGetValue(mapName, out var data)) { if (data.Obstacles != null) foreach (Vector2 obs 
in data.Obstacles) if (obs.X >= 0 && obs.X < w && obs.Y >= 0 && obs.Y < h) grid[(int)obs.X, (int)obs.Y] = 1;
                if (data.Walkables != null) foreach (Vector2 walk in data.Walkables) if (walk.X >= 0 && walk.X < w 
&& walk.Y >= 0 && walk.Y < h) grid[(int)walk.X, (int)walk.Y] = 0;
            } if (tempEdits.TryGetValue(mapName, out var edits)) { foreach (var kvp in edits) { if (kvp.Key.X >= 0 
&& kvp.Key.X < w && kvp.Key.Y >= 0 && kvp.Key.Y < h) grid[kvp.Key.X, kvp.Key.Y] = kvp.Value;
                } } }
        
        public static Point?
            FindTransitionTile(GameLocation location, string targetMapName) { foreach (var door in location.doors.Pairs) if (door.Value.Equals(targetMapName, StringComparison.OrdinalIgnoreCase)) return new Point(door.Key.X, door.Key.Y);
            foreach (Warp w in location.warps) if (w.TargetName.Equals(targetMapName, StringComparison.OrdinalIgnoreCase)) return new Point(w.X, w.Y);
            foreach (var building in location.buildings) { if (building.indoors.Value != null && building.indoors.Value.Name.Equals(targetMapName, StringComparison.OrdinalIgnoreCase)) return building.getPointForHumanDoor();
                if (building.buildingType.Value != null && building.buildingType.Value.Contains("Farmhouse", StringComparison.OrdinalIgnoreCase) && targetMapName.StartsWith("FarmHouse")) return building.getPointForHumanDoor();
            } if (location.Map != null && location.Map.Layers.Count > 0) { 
                for (int x = 0; x < location.Map.Layers[0].LayerWidth; x++) { 
                    for (int y = 0; y < location.Map.Layers[0].LayerHeight; y++) { 
                        string? action = location.doesTileHaveProperty(x, y, "Action", "Buildings");
                        if (!string.IsNullOrEmpty(action)) {
                            if (action.IndexOf(targetMapName, StringComparison.OrdinalIgnoreCase) >= 0) return new Point(x, y);
                            if (action.Contains("MinecartTransport") && (targetMapName == "Mine" || targetMapName == "Town" || targetMapName == "Mountain" || targetMapName == "BusStop")) return new Point(x, y);
                            if (action.Contains("BusTicket") && targetMapName == "Desert") return new Point(x, y);
                            if (action.Contains("DesertBus") && targetMapName == "BusStop") return new Point(x, y);
                            if (action.Contains("MineElevator") && targetMapName.StartsWith("UndergroundMine")) return new Point(x, y);
                            if (action.Contains("BoatTicket") && targetMapName.StartsWith("Island")) return new Point(x, y);
                            if (action.Contains("ParrotExpress") && targetMapName.StartsWith("Island")) return new Point(x, y);
                        }
                        string warpAction = location.doesTileHaveProperty(x, y, "Warp", "Buildings");
                        if (!string.IsNullOrEmpty(warpAction) && warpAction.IndexOf(targetMapName, StringComparison.OrdinalIgnoreCase) >= 0) return new Point(x, y);
                    } 
                } 
            } if (targetMapName.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase) && location.Name.Equals("Farm", StringComparison.OrdinalIgnoreCase)) return new Point(64, 15); return null;
        }

        public static bool IsTileStrictlyDoorArea(GameLocation location, int x, int y) 
        { 
            foreach (Warp w in location.warps) if (w.X == x && w.Y == y) return true;
            foreach (var door in location.doors.Pairs) if (door.Key.X == x && door.Key.Y == y) return true;
            
            Point? mainHouseDoor = null;
            if (location.Name.Equals("Farm", StringComparison.OrdinalIgnoreCase)) mainHouseDoor = new Point(64, 15);

            foreach (var building in location.buildings) { 
                Point door = building.getPointForHumanDoor();
                if (door.X == x && door.Y == y) return true;
                
                if (building.buildingType.Value != null && building.buildingType.Value.Contains("Farmhouse", StringComparison.OrdinalIgnoreCase))
                {
                    mainHouseDoor = door;
                }

                // Logic cho Cabin (Nhà người chơi phụ)
                if (building.buildingType.Value != null && building.buildingType.Value.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    int dx = door.X;
                    int dy = door.Y;
                    if (y == dy + 1 && x >= dx - 2 && x <= dx + 1) return true; // Xuống 1 ô: 4 ô, cửa nằm ở ô thứ 3
                }
            } 

            if (mainHouseDoor.HasValue)
            {
                int dx = mainHouseDoor.Value.X;
                int dy = mainHouseDoor.Value.Y;
                if (x == dx && y == dy) return true; // Cửa chính
                if (y == dy + 1 && x >= dx - 4 && x <= dx + 2) return true; // Xuống 1 ô: 7 ô, cửa nằm ở ô thứ 5
                if (y == dy + 2 && x >= dx - 1 && x <= dx + 1) return true; // Xuống 2 ô: 3 ô, cửa nằm ở ô thứ 2
            }

            if (location.Map != null && location.Map.Layers.Count > 0 && x >= 0 && y >= 0 && x < location.Map.Layers[0].LayerWidth && y < location.Map.Layers[0].LayerHeight) { 
                string? adjAction = location.doesTileHaveProperty(x, y, "Action", "Buildings");
                if (!string.IsNullOrEmpty(adjAction) && (adjAction.StartsWith("Warp", StringComparison.OrdinalIgnoreCase) || adjAction.Contains("FarmHouse") || adjAction.Contains("MinecartTransport") || adjAction.Contains("BusTicket") || adjAction.Contains("DesertBus") || adjAction.Contains("MineElevator") || adjAction.Contains("BoatTicket") || adjAction.Contains("ParrotExpress"))) return true; 
                string? warpAction = location.doesTileHaveProperty(x, y, "Warp", "Buildings");
                if (!string.IsNullOrEmpty(warpAction) && (warpAction.StartsWith("Warp", StringComparison.OrdinalIgnoreCase) || warpAction.Contains("FarmHouse"))) return true;
            } 
            return false;
        }


        public static bool IsTileReallyPassable(GameLocation location, int x, int y) 
        { 
            if (IsTileStrictlyDoorArea(location, x, y)) return true;
            
            xTile.Dimensions.Location tileLoc = new xTile.Dimensions.Location(x, y); 
            
            // [FIX LỖI VIEWPORT]: Quét toàn bộ map thay vì chỉ quét chỗ hiển thị màn hình
            xTile.Dimensions.Rectangle fullMapViewport = new xTile.Dimensions.Rectangle(0, 0, location.Map.DisplayWidth, location.Map.DisplayHeight);
            if (!location.isTilePassable(tileLoc, fullMapViewport)) return false; 
            
            // [FIX ĐI XUỐNG NƯỚC]: Chặn luôn map nước
            if (location.isWaterTile(x, y)) return false;

            Rectangle tileRect = new Rectangle(x * Game1.tileSize + 8, y * Game1.tileSize + 8, Game1.tileSize - 16, Game1.tileSize - 16);
            
            foreach (var building in location.buildings) if (building.occupiesTile(new Vector2(x,y)) || building.intersects(tileRect)) return false;
            foreach (var clump in location.resourceClumps) if (clump.getBoundingBox().Intersects(tileRect)) return false;
            foreach (var ltf in location.largeTerrainFeatures) if (ltf.getBoundingBox().Intersects(tileRect)) return false;
            
            if (location.terrainFeatures.TryGetValue(new Vector2(x, y), out StardewValley.TerrainFeatures.TerrainFeature? feat)) 
            {
                if (feat != null && !feat.isPassable()) return false; 
            }

            Vector2 tile = new Vector2(x, y);
            
            // [FIX NHẬN DIỆN RÁC]: Bắt đích danh những vật thường có hitbox lỗi
            if (location.objects.TryGetValue(tile, out StardewValley.Object? obj)) 
            {
                if (!obj.isPassable() || 
                    obj.Name.IndexOf("Weed", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    obj.Name.IndexOf("Stone", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    obj.Name.IndexOf("Twig", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    obj.Name.IndexOf("Branch", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    obj.Name.IndexOf("Rock", StringComparison.OrdinalIgnoreCase) >= 0 || 
                    obj.Name.IndexOf("Wood", StringComparison.OrdinalIgnoreCase) >= 0) 
                {
                    return false; 
                }
            }
            
            foreach (var furniture in location.furniture) { 
                if (furniture.GetBoundingBox().Intersects(tileRect)) { 
                    
                    if (furniture is StardewValley.Objects.BedFurniture 
                        || furniture.Name.IndexOf("Bed", StringComparison.OrdinalIgnoreCase) >= 0 
                        || furniture.furniture_type.Value == 14 
                        || furniture.furniture_type.Value == 15) 
                    {
                        int localY = y - (int)furniture.TileLocation.Y;
                        if (localY == 0 || localY == 2) return false;
                    } 
                    else if (furniture.furniture_type.Value == 12 || furniture.furniture_type.Value == 13 || furniture.furniture_type.Value == 17) 
                    {
                        // Cho qua thảm, để hàm lọt qua check xem có hòn đá đè lên thảm không
                    }
                    else if (!furniture.isPassable()) 
                    {
                        return false;
                    }
                } 
            } 
            
            foreach (var building in location.buildings) if (building.occupiesTile(tile)) return false;
            foreach (var clump in location.resourceClumps) if (clump.getBoundingBox().Intersects(tileRect)) return false;
            foreach (var ltf in location.largeTerrainFeatures) if (ltf.getBoundingBox().Intersects(tileRect)) return false;
            
            if (location.terrainFeatures.TryGetValue(tile, out StardewValley.TerrainFeatures.TerrainFeature? feature)) 
            {
                if (feature != null && !feature.isPassable()) return false; 
            }
            
            return true;
        }

        public static List<Point>?
            FindBedSequence(GameLocation? location)
        {
            if (location == null) return null;
            Point targetBedSpot = Point.Zero;
            bool foundBedSpot = false;
            
            if (location is StardewValley.Locations.FarmHouse farmHouse)
            {
                targetBedSpot = farmHouse.GetPlayerBedSpot();
                foundBedSpot = true;
            }

            foreach (var f in location.furniture)
            {
                if (f is StardewValley.Objects.BedFurniture || f.furniture_type.Value == 14 || f.furniture_type.Value == 15 || f.Name.Contains("Bed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!foundBedSpot) {
                        int w = f.GetBoundingBox().Width / Game1.tileSize;
                        int startX = (int)f.TileLocation.X;
                        int startY = (int)f.TileLocation.Y;
                        targetBedSpot = new Point(startX + (w >= 2 ? 1 : 0), startY + 1);
                    }
                    return new List<Point> { targetBedSpot };
                }
            }
            
            if (foundBedSpot) return new List<Point> { targetBedSpot };
            return null;
        }

        private void AutoFaceWater(GameLocation location, Vector2 tile)
        {
            if (location == null) return;
            int x = (int)tile.X;
            int y = (int)tile.Y;
            
            Point[] directions = new Point[] 
            { 
                new Point(0, -1), 
                new Point(1, 0),  
                new Point(0, 1),  
               
                new Point(-1, 0)  
            };
            for (int i = 0; i < directions.Length; i++)
            {
                for (int dist = 1; dist <= 3; dist++)
                {
                    int checkX = x + (directions[i].X * dist);
                    int checkY = y + (directions[i].Y * dist);
                    
                    if (location.isWaterTile(checkX, checkY))
                    {
                        Game1.player.FacingDirection = i;
                        Game1.player.faceDirection(i); 
                        return; 
                    }
                }
            }
        }

        public static bool Prefix_FishingRod_tickUpdate(StardewValley.Tools.FishingRod __instance, Microsoft.Xna.Framework.GameTime time, StardewValley.Farmer who)
        {
            if (isBotHoldingCast && __instance.isTimingCast && who == Game1.player)
            {
                __instance.castingPower += 0.001f * time.ElapsedGameTime.Milliseconds;
                if (__instance.castingPower >= botTargetCastPower)
                {
                    __instance.castingPower = botTargetCastPower;
                    isBotHoldingCast = false; // Nhả nút, frame tiếp theo game gốc sẽ cast
                }
                return false; // Chặn game gốc nhảy vọt thanh lực
            }
            return true;
        }

        public static bool Prefix_updateMovementAnimation(StardewValley.Farmer __instance)
        {
            if (shouldBlockHalt && __instance == Game1.player)
            {
                __instance.movementDirections.Clear();
                __instance.movementDirections.Add(__instance.FacingDirection);
                __instance.running = true;
            }
            return true;
        }
        
        public static void Postfix_updateMovementAnimation(StardewValley.Farmer __instance)
        {
            if (shouldBlockHalt && __instance == Game1.player)
            {
                __instance.movementDirections.Clear();
            }
        }
        
        public static bool Prefix_Halt(StardewValley.Farmer __instance)
        {
            if (shouldBlockHalt && __instance == Game1.player) return false;
            return true;
        }
    }
}
