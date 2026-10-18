using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KSP.UI;
using KSP.UI.Screens;
using SaveUpgradePipeline;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// KSP game-scene navigation tools. Each action replicates the code path that
    /// the corresponding in-game button (main menu, pause menu, KSC buildings,
    /// tracking station, alarm clock "switch to", revert dialog) runs, including
    /// the same permission and safety checks. Scene-changing tools wait for the
    /// new scene to finish loading before returning.
    /// </summary>
    public class KspSceneTools
    {
        readonly ToolRegistry _tools;

        // Sequence numbers of scene load requests and GUI-ready notifications,
        // used to tell whether an action started a load and when it finished.
        int _requestSeq;
        int _readySeq;
        GameScenes _lastRequestedScene;
        float _lastRequestTime;

        // A load that hasn't reported ready after this long is assumed to have
        // completed without the event firing, so it doesn't block tools forever.
        const float StalePendingLoadSeconds = 600f;

        const float DefaultTimeoutSeconds = 180f;

        static readonly string[] FacilityNames =
        {
            "VAB",
            "SPH",
            "TrackingStation",
            "AstronautComplex",
            "Administration",
            "MissionControl",
            "RnD",
        };

        public KspSceneTools(ToolRegistry tools)
        {
            _tools = tools;
        }

        #region Parameter Types

        public struct TimeoutParams
        {
            [McpParam("Max seconds to wait for the scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        public struct ListVesselsParams
        {
            [McpParam(
                "Only include vessels owned by the player, i.e. ones that can be flown (default true). Set false to include asteroids, comets and other unowned objects."
            )]
            public bool? OwnedOnly { get; set; }

            [McpParam("Max results (default 100)")]
            public int? Limit { get; set; }

            [McpParam("Skip first N results (default 0)")]
            public int? Offset { get; set; }
        }

        public struct EnterSaveParams
        {
            [McpParam(
                "Name of the save folder under saves/ (as returned by ksp_list_saves).",
                Required = true
            )]
            public string Save { get; set; }

            [McpParam("Max seconds to wait for the scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        public struct LoadSaveParams
        {
            [McpParam(
                "Save file name within the current save folder, without the .sfs extension (e.g. 'quicksave', 'persistent'). See ksp_list_saves.",
                Required = true
            )]
            public string Name { get; set; }

            [McpParam("Max seconds to wait for the scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        public struct SwitchVesselParams
        {
            [McpParam("Vessel GUID (vesselId from ksp_list_vessels).")]
            public string VesselId { get; set; }

            [McpParam("Vessel persistentId (from ksp_list_vessels).")]
            public long? PersistentId { get; set; }

            [McpParam("Vessel display name (case-insensitive). Must match exactly one vessel.")]
            public string Name { get; set; }

            [McpParam("Max seconds to wait for the switch to finish (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        public struct EnterFacilityParams
        {
            [McpParam(
                "Facility to enter. VAB, SPH and TrackingStation change scene; the others open their overlay UI within the KSC scene.",
                Required = true,
                EnumValues = new[]
                {
                    "VAB",
                    "SPH",
                    "TrackingStation",
                    "AstronautComplex",
                    "Administration",
                    "MissionControl",
                    "RnD",
                }
            )]
            public string Facility { get; set; }

            [McpParam("Max seconds to wait for the scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        public struct LeaveFlightParams
        {
            [McpParam(
                "If the vessel is not in a state that can be saved (e.g. under acceleration, in atmosphere), leave anyway and discard everything since the last save. Mirrors the 'revert to last save and leave' option in the pause menu. Default false."
            )]
            public bool? DiscardUnsavedProgress { get; set; }

            [McpParam("Max seconds to wait for the scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        #endregion

        public void Register()
        {
            GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelWasLoadedGUIReady);

            _tools.Register(
                "ksp_get_game_state",
                "Get the current KSP game scene, loaded save, active vessel, and which navigation actions (revert, leave to KSC/tracking station, quickload, etc.) are currently available.",
                GetGameState
            );

            _tools.Register(
                "ksp_list_saves",
                "List saves. In the main menu this lists save folders (games) that ksp_enter_save can open. In game it lists the .sfs files in the current save folder that ksp_load_save can load.",
                ListSaves
            );

            _tools.Register<ListVesselsParams>(
                "ksp_list_vessels",
                "List vessels in the current game, with the ids accepted by ksp_switch_vessel.",
                ListVessels
            );

            _tools.RegisterCoroutine<EnterSaveParams>(
                "ksp_enter_save",
                "Main menu only. Resume a saved game, exactly like Start Game > Resume Saved in the main menu. Waits until the game's starting scene has loaded.",
                EnterSave
            );

            _tools.RegisterCoroutine<LoadSaveParams>(
                "ksp_load_save",
                "In game (any game scene). Load a save file from the current save folder, like the pause menu's Load button / quickload. Saves made in flight resume the flight; others go to the Space Center. Waits until loading has finished.",
                LoadSave
            );

            _tools.RegisterCoroutine<SwitchVesselParams>(
                "ksp_switch_vessel",
                "In game (any game scene). Switch to (fly) a vessel, like the alarm clock / tracking station 'Fly' / map view 'Switch To' buttons. In flight, nearby loaded vessels are switched to in place; otherwise the flight scene is (re)loaded focused on the vessel. Identify the vessel by vesselId, persistentId or name.",
                SwitchVessel
            );

            _tools.RegisterCoroutine<EnterFacilityParams>(
                "ksp_enter_facility",
                "Space Center scene only. Enter a KSC building, exactly like clicking on it. Fails if the facility is locked, closed or destroyed.",
                EnterFacility
            );

            _tools.RegisterCoroutine<LeaveFlightParams>(
                "ksp_enter_tracking_station",
                "Space Center or flight scene. Go to the Tracking Station: in the KSC by clicking the building, in flight via the pause menu's Tracking Station button (which saves the game first).",
                EnterTrackingStation
            );

            _tools.RegisterCoroutine<LeaveFlightParams>(
                "ksp_return_to_space_center",
                "Flight, editor or tracking station. Return to the Space Center via that scene's own exit button. In flight this saves first (pause menu Space Center button). In the editor the current ship is kept in the editor cache but NOT saved to a craft file.",
                ReturnToSpaceCenter
            );

            _tools.RegisterCoroutine<TimeoutParams>(
                "ksp_revert_to_launch",
                "Flight only. Revert the flight to launch, like the pause menu's 'Revert to Launch' option. Only available if the current flight can be reverted.",
                RevertToLaunch
            );

            _tools.RegisterCoroutine<TimeoutParams>(
                "ksp_revert_to_editor",
                "Flight only. Revert the flight to the VAB or SPH it was built in, like the pause menu's 'Revert to Vehicle Assembly/Spaceplane Hangar' option. Only available if the current flight can be reverted.",
                RevertToEditor
            );
        }

        #region Scene Tracking

        void OnGameSceneLoadRequested(GameScenes scene)
        {
            _requestSeq++;
            _lastRequestedScene = scene;
            _lastRequestTime = Time.realtimeSinceStartup;
        }

        void OnLevelWasLoadedGUIReady(GameScenes scene)
        {
            _readySeq = _requestSeq;
        }

        bool SceneLoadPending =>
            _readySeq < _requestSeq
            && Time.realtimeSinceStartup - _lastRequestTime < StalePendingLoadSeconds;

        /// <summary>
        /// Run an action that is expected to start a scene load, then wait for
        /// that load (and any loads it chains into) to complete. The trigger
        /// returns an error message, or null if it started the load. Actions
        /// that request the load a few frames later can pass a grace period to
        /// wait for the request.
        /// </summary>
        internal IEnumerator RunSceneChange(
            string action,
            float? timeoutSeconds,
            Func<string> trigger,
            Action<McpProtocol.ToolCallResult> callback,
            float requestGraceSeconds = 0f
        )
        {
            if (SceneLoadPending)
            {
                callback(
                    McpProtocol.ToolError(
                        $"A scene change to {_lastRequestedScene} is already in progress."
                    )
                );
                yield break;
            }

            var previousScene = HighLogic.LoadedScene;
            int startSeq = _requestSeq;

            string error = trigger();
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            float graceStart = Time.realtimeSinceStartup;
            while (
                _requestSeq == startSeq
                && Time.realtimeSinceStartup - graceStart < requestGraceSeconds
            )
                yield return null;

            if (_requestSeq == startSeq)
            {
                callback(
                    McpProtocol.ToolError(
                        $"{action} did not start a scene change. The game may have shown a message or dialog explaining why; check ksp_get_game_state and take a screenshot."
                    )
                );
                yield break;
            }

            yield return WaitForSceneReady(previousScene, timeoutSeconds, callback);
        }

        IEnumerator WaitForSceneReady(
            GameScenes previousScene,
            float? timeoutSeconds,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            float timeout = timeoutSeconds ?? DefaultTimeoutSeconds;
            float start = Time.realtimeSinceStartup;

            // Wait for the requested scene (and any scene it immediately
            // redirects to) to finish loading, then for the scene's main
            // systems to come up.
            while (SceneLoadPending || !SceneSystemsReady())
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    callback(
                        McpProtocol.ToolError(
                            $"Timed out after {timeout:0}s waiting for scene {_lastRequestedScene} to finish loading (current scene: {HighLogic.LoadedScene}). It may still be loading; check ksp_get_game_state."
                        )
                    );
                    yield break;
                }
                yield return null;
            }

            // Let Start() / first-frame UI setup run.
            for (int i = 0; i < 5; i++)
                yield return null;

            var result = new JsonObject
            {
                ["previousScene"] = previousScene.ToString(),
                ["scene"] = HighLogic.LoadedScene.ToString(),
                ["elapsedSeconds"] = Math.Round(Time.realtimeSinceStartup - start, 2),
            };
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                result["activeVessel"] = VesselSummary(FlightGlobals.ActiveVessel);
            if (HighLogic.LoadedSceneIsEditor)
                result["editorFacility"] = EditorDriver.editorFacility.ToString();

            callback(McpProtocol.ToolSuccess(result));
        }

        static bool SceneSystemsReady()
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.FLIGHT:
                    return FlightGlobals.ready && FlightGlobals.ActiveVessel != null;
                case GameScenes.EDITOR:
                    return EditorLogic.fetch != null;
                case GameScenes.TRACKSTATION:
                    return SpaceTracking.Instance != null;
                default:
                    return true;
            }
        }

        #endregion

        #region Info Tools

        McpProtocol.ToolCallResult GetGameState()
        {
            var game = HighLogic.CurrentGame;
            var state = new JsonObject
            {
                ["scene"] = HighLogic.LoadedScene.ToString(),
                ["sceneLoadPending"] = SceneLoadPending,
                ["isGameScene"] = HighLogic.LoadedSceneIsGame,
            };
            if (SceneLoadPending)
                state["loadingScene"] = _lastRequestedScene.ToString();

            if (!HighLogic.LoadedSceneIsGame || game == null)
                return McpProtocol.ToolSuccess(state);

            state["saveFolder"] = HighLogic.SaveFolder;
            state["gameTitle"] = game.Title;
            state["gameMode"] = game.Mode.ToString();
            state["universalTime"] = Planetarium.GetUniversalTime();

            var p = game.Parameters;
            var actions = new JsonObject
            {
                ["loadSave"] = p.Flight.CanQuickLoad,
                ["switchVessel"] = true,
            };

            switch (HighLogic.LoadedScene)
            {
                case GameScenes.SPACECENTER:
                    actions["enterTrackingStation"] = p.SpaceCenter.CanGoInTrackingStation;
                    actions["enterVAB"] = p.SpaceCenter.CanGoInVAB;
                    actions["enterSPH"] = p.SpaceCenter.CanGoInSPH;
                    break;

                case GameScenes.EDITOR:
                    state["editorFacility"] = EditorDriver.editorFacility.ToString();
                    actions["returnToSpaceCenter"] = p.Editor.CanLeaveToSpaceCenter;
                    break;

                case GameScenes.TRACKSTATION:
                    actions["returnToSpaceCenter"] = p.TrackingStation.CanLeaveToSpaceCenter;
                    break;

                case GameScenes.FLIGHT:
                {
                    var vessel = FlightGlobals.ActiveVessel;
                    if (vessel != null)
                    {
                        state["activeVessel"] = VesselSummary(vessel);
                        state["clearToSave"] = FlightGlobals.ClearToSave(logMsg: false).ToString();
                    }

                    actions["returnToSpaceCenter"] = p.Flight.CanLeaveToSpaceCenter;
                    actions["enterTrackingStation"] =
                        p.Flight.CanLeaveToTrackingStation && p.SpaceCenter.CanGoInTrackingStation;
                    actions["revertToLaunch"] = CanRevertToLaunch(out _);
                    bool canRevertToEditor = CanRevertToEditor(out _);
                    actions["revertToEditor"] = canRevertToEditor;
                    if (canRevertToEditor)
                        actions["revertToEditorFacility"] = ShipConstruction.ShipType.ToString();
                    break;
                }
            }

            state["availableActions"] = actions;
            return McpProtocol.ToolSuccess(state);
        }

        McpProtocol.ToolCallResult ListSaves()
        {
            var saves = new JsonArray();
            string savesRoot = KSPUtil.ApplicationRootPath + "saves/";

            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                // Same folders the main menu's Resume Saved dialog shows.
                string[] excluded =
                {
                    "training",
                    "scenarios",
                    "missions",
                    "test_missions",
                    ".svn",
                };
                foreach (var dir in new DirectoryInfo(savesRoot).GetDirectories())
                {
                    if (excluded.Contains(dir.Name))
                        continue;
                    var file = new FileInfo(Path.Combine(dir.FullName, "persistent.sfs"));
                    saves.Add(
                        new JsonObject
                        {
                            ["save"] = dir.Name,
                            ["hasPersistent"] = file.Exists && file.Length > 0,
                            ["lastWriteTime"] = file.Exists
                                ? file.LastWriteTime.ToString("o")
                                : null,
                        }
                    );
                }

                return McpProtocol.ToolSuccess(
                    new JsonObject { ["kind"] = "saveFolders", ["saves"] = saves }
                );
            }

            if (!HighLogic.LoadedSceneIsGame || string.IsNullOrEmpty(HighLogic.SaveFolder))
                return McpProtocol.ToolError(
                    $"Saves can only be listed from the main menu or a game scene (current scene: {HighLogic.LoadedScene})."
                );

            var files = new DirectoryInfo(savesRoot + HighLogic.SaveFolder)
                .GetFiles("*.sfs")
                .OrderByDescending(f => f.LastWriteTime);
            foreach (var file in files)
            {
                saves.Add(
                    new JsonObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file.Name),
                        ["lastWriteTime"] = file.LastWriteTime.ToString("o"),
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["kind"] = "saveFiles",
                    ["saveFolder"] = HighLogic.SaveFolder,
                    ["saves"] = saves,
                }
            );
        }

        McpProtocol.ToolCallResult ListVessels(ListVesselsParams args)
        {
            if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null)
                return McpProtocol.ToolError(
                    $"Not in a game scene (current scene: {HighLogic.LoadedScene})."
                );

            bool ownedOnly = args.OwnedOnly ?? true;
            int limit = args.Limit ?? 100;
            int offset = args.Offset ?? 0;

            var all = GetVesselEntries().Where(v => !ownedOnly || v.Owned).ToList();
            var vessels = new JsonArray();
            foreach (var entry in all.Skip(offset).Take(limit))
                vessels.Add(entry.ToJson());

            return McpProtocol.ToolSuccess(
                new JsonObject { ["totalCount"] = all.Count, ["vessels"] = vessels }
            );
        }

        #endregion

        #region Main Menu

        IEnumerator EnterSave(EnterSaveParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            return RunSceneChange(
                "Entering save",
                args.TimeoutSeconds,
                () =>
                {
                    if (HighLogic.LoadedScene != GameScenes.MAINMENU)
                        return $"ksp_enter_save only works from the main menu (current scene: {HighLogic.LoadedScene}). Use ksp_load_save to load a save while in game.";
                    if (UnityEngine.Object.FindObjectOfType<MainMenu>() == null)
                        return "The main menu is not ready yet.";

                    string save = args.Save;
                    if (string.IsNullOrEmpty(save) || save.IndexOfAny(new[] { '/', '\\' }) >= 0)
                        return $"Invalid save folder name '{save}'.";

                    // Replicates MainMenu.OnLoadDialogFinished.
                    ConfigNode node = GamePersistence.LoadSFSFile("persistent", save);
                    if (node == null)
                        return $"Could not load saves/{save}/persistent.sfs. Use ksp_list_saves to see available saves.";

                    string root = KSPUtil.ApplicationRootPath + "saves/" + save;
                    Directory.CreateDirectory(root + "/Ships");
                    Directory.CreateDirectory(root + "/Ships/VAB");
                    Directory.CreateDirectory(root + "/Ships/SPH");

                    return RunUpgradePipeline(
                        node,
                        save,
                        n =>
                        {
                            // Replicates MainMenu.OnLoadDialogPipelineFinished.
                            HighLogic.CurrentGame = GamePersistence.LoadGameCfg(
                                n,
                                save,
                                nullIfIncompatible: true,
                                suppressIncompatibleMessage: false
                            );
                            if (HighLogic.CurrentGame == null)
                                return $"Save '{save}' is incompatible with this version of KSP.";

                            GamePersistence.UpdateScenarioModules(HighLogic.CurrentGame);
                            GamePersistence.SaveGame(
                                HighLogic.CurrentGame,
                                "persistent",
                                save,
                                SaveMode.OVERWRITE
                            );
                            GameEvents.onGameStatePostLoad.Fire(n);
                            HighLogic.SaveFolder = save;
                            HighLogic.CurrentGame.Start();
                            return null;
                        }
                    );
                },
                callback
            );
        }

        /// <summary>
        /// Run a save through KSPUpgradePipeline, as all stock load paths do.
        /// If the upgrade fails KSP shows a modal dialog asking the user what to
        /// do; we report that instead of waiting on it.
        /// </summary>
        static string RunUpgradePipeline(
            ConfigNode node,
            string saveName,
            Func<ConfigNode, string> onSuccess
        )
        {
            string result = null;
            bool finished = false;

            KSPUpgradePipeline.Process(
                node,
                saveName,
                LoadContext.SFS,
                n =>
                {
                    finished = true;
                    result = onSuccess(n);
                },
                (opt, n) =>
                {
                    finished = true;
                    result = $"Save upgrade for '{saveName}' failed ({opt}).";
                }
            );

            if (!finished)
                return $"The save '{saveName}' could not be upgraded to this KSP version. The game is showing a dialog asking whether to load it anyway; resolve it manually.";
            return result;
        }

        #endregion

        #region Load Save

        IEnumerator LoadSave(LoadSaveParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            return RunSceneChange(
                "Loading save",
                args.TimeoutSeconds,
                () =>
                {
                    if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null)
                        return $"ksp_load_save only works in a game scene (current scene: {HighLogic.LoadedScene}). Use ksp_enter_save from the main menu.";
                    if (!HighLogic.CurrentGame.Parameters.Flight.CanQuickLoad)
                        return "Loading saves is disabled for this game (difficulty setting).";

                    string name = args.Name;
                    if (name.EndsWith(".sfs", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 4);
                    if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                        return $"Invalid save name '{args.Name}'.";

                    string folder = HighLogic.SaveFolder;

                    // Replicates QuickSaveLoad.quickLoad / KSCPauseMenu.quickLoad.
                    ROCManager.Instance?.ClearROCsCache();
                    ConfigNode node = GamePersistence.LoadSFSFile(name, folder);
                    if (node == null)
                        return $"Could not load saves/{folder}/{name}.sfs. Use ksp_list_saves to see available saves.";

                    var originalVersion = NodeUtil.GetCfgVersion(node, LoadContext.SFS);
                    return RunUpgradePipeline(
                        node,
                        folder,
                        n =>
                        {
                            Game game = GamePersistence.LoadGameCfg(
                                n,
                                folder,
                                nullIfIncompatible: true,
                                suppressIncompatibleMessage: false
                            );
                            if (game == null || game.flightState == null || !game.compatible)
                                return $"Save '{name}' is incompatible or has no flight state.";

                            GamePersistence.UpdateScenarioModules(game);
                            GameEvents.onGameStatePostLoad.Fire(n);
                            if (
                                game.startScene != GameScenes.FLIGHT
                                && !(originalVersion < new Version(0, 24, 0))
                            )
                            {
                                GamePersistence.SaveGame(
                                    game,
                                    "persistent",
                                    folder,
                                    SaveMode.OVERWRITE
                                );
                                HighLogic.LoadScene(GameScenes.SPACECENTER);
                            }
                            else
                            {
                                FlightDriver.StartAndFocusVessel(
                                    game,
                                    game.flightState.activeVesselIdx
                                );
                            }
                            return null;
                        }
                    );
                },
                callback
            );
        }

        #endregion

        #region Switch Vessel

        IEnumerator SwitchVessel(
            SwitchVesselParams args,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null)
            {
                callback(
                    McpProtocol.ToolError(
                        $"ksp_switch_vessel only works in a game scene (current scene: {HighLogic.LoadedScene})."
                    )
                );
                yield break;
            }

            string findError = FindVessel(args, out var target);
            if (findError != null)
            {
                callback(McpProtocol.ToolError(findError));
                yield break;
            }

            if (!target.Owned)
            {
                callback(
                    McpProtocol.ToolError(
                        $"'{target.Name}' is not owned by the player and cannot be flown."
                    )
                );
                yield break;
            }

            // Loaded vessel in flight: switch in place without a scene change.
            if (HighLogic.LoadedSceneIsFlight && target.Vessel != null && target.Vessel.loaded)
            {
                if (target.Vessel == FlightGlobals.ActiveVessel)
                {
                    callback(
                        McpProtocol.ToolSuccess(
                            new JsonObject
                            {
                                ["scene"] = HighLogic.LoadedScene.ToString(),
                                ["activeVessel"] = VesselSummary(target.Vessel),
                                ["note"] = "Vessel was already active.",
                            }
                        )
                    );
                    yield break;
                }

                string error = CheckFlightSwitchAllowed(target.Vessel);
                if (error != null)
                {
                    callback(McpProtocol.ToolError(error));
                    yield break;
                }

                int seq = _requestSeq;
                if (!FlightGlobals.SetActiveVessel(target.Vessel))
                {
                    callback(McpProtocol.ToolError($"KSP refused to switch to '{target.Name}'."));
                    yield break;
                }

                // A loaded vessel never triggers a scene load, but be safe.
                if (_requestSeq != seq)
                {
                    yield return WaitForSceneReady(
                        GameScenes.FLIGHT,
                        args.TimeoutSeconds,
                        callback
                    );
                    yield break;
                }

                float start = Time.realtimeSinceStartup;
                float timeout = args.TimeoutSeconds ?? DefaultTimeoutSeconds;
                while (!FlightGlobals.ready || FlightGlobals.ActiveVessel != target.Vessel)
                {
                    if (Time.realtimeSinceStartup - start > timeout)
                    {
                        callback(McpProtocol.ToolError("Timed out waiting for the vessel switch."));
                        yield break;
                    }
                    yield return null;
                }

                callback(
                    McpProtocol.ToolSuccess(
                        new JsonObject
                        {
                            ["scene"] = HighLogic.LoadedScene.ToString(),
                            ["activeVessel"] = VesselSummary(FlightGlobals.ActiveVessel),
                            ["elapsedSeconds"] = Math.Round(Time.realtimeSinceStartup - start, 2),
                        }
                    )
                );
                yield break;
            }

            yield return RunSceneChange(
                "Switching vessel",
                args.TimeoutSeconds,
                () => SwitchToUnloadedVessel(target),
                callback
            );
        }

        static string CheckFlightSwitchAllowed(Vessel vessel)
        {
            var p = HighLogic.CurrentGame.Parameters.Flight;
            if (vessel.loaded ? !p.CanSwitchVesselsNear : !p.CanSwitchVesselsFar)
                return "Switching to this vessel is disabled for this game (difficulty setting).";

            if (FlightGlobals.ActiveVessel != null)
            {
                var status = FlightGlobals.ClearToSave(logMsg: false);
                if (status != ClearToSaveStatus.CLEAR)
                    return $"Cannot switch vessels right now: {status}.";
            }
            return null;
        }

        string SwitchToUnloadedVessel(VesselEntry target)
        {
            if (HighLogic.LoadedSceneIsFlight)
            {
                // Same path as the map view's Switch To: FlightGlobals saves and
                // reloads the flight scene for unloaded vessels.
                string error = CheckFlightSwitchAllowed(target.Vessel);
                if (error != null)
                    return error;
                if (!FlightGlobals.SetActiveVessel(target.Vessel))
                    return $"KSP refused to switch to '{target.Name}'.";
                return null;
            }

            if (HighLogic.LoadedSceneIsEditor)
            {
                // No stock in-editor vessel switch exists; leave the editor the
                // way its exit button does (keeping the ship in the editor
                // cache) and fly the vessel from the saved state.
                var editor = EditorLogic.fetch;
                if (editor != null && editor.ship != null)
                    ShipConstruction.CreateBackup(editor.ship);
                if (CrewAssignmentDialog.Instance != null)
                    ShipConstruction.ShipManifest = CrewAssignmentDialog.Instance.GetManifest();
                EditorDriver.saveselectedLaunchSite();
                GamePersistence.SaveGame(
                    "persistent",
                    HighLogic.SaveFolder,
                    SaveMode.OVERWRITE,
                    GameScenes.FLIGHT
                );
                FlightDriver.StartAndFocusVessel("persistent", target.Index);
                return null;
            }

            // Space Center / Tracking Station: replicates the alarm clock's
            // Switch To and the tracking station's Fly button.
            if (
                HighLogic.LoadedScene == GameScenes.TRACKSTATION
                && !HighLogic.CurrentGame.Parameters.TrackingStation.CanFlyVessel
            )
                return "Flying vessels from the Tracking Station is disabled for this game.";
            GamePersistence.SaveGame(
                "persistent",
                HighLogic.SaveFolder,
                SaveMode.OVERWRITE,
                GameScenes.FLIGHT
            );
            FlightDriver.StartAndFocusVessel(
                "persistent",
                FlightGlobals.Vessels.IndexOf(target.Vessel)
            );
            return null;
        }

        #endregion

        #region Space Center

        IEnumerator EnterFacility(
            EnterFacilityParams args,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER)
            {
                callback(
                    McpProtocol.ToolError(
                        $"ksp_enter_facility only works in the Space Center scene (current scene: {HighLogic.LoadedScene})."
                    )
                );
                yield break;
            }

            string facility = FacilityNames.FirstOrDefault(n =>
                string.Equals(n, args.Facility, StringComparison.OrdinalIgnoreCase)
            );
            if (facility == null)
            {
                callback(
                    McpProtocol.ToolError(
                        $"Unknown facility '{args.Facility}'. Valid values: {string.Join(", ", FacilityNames)}."
                    )
                );
                yield break;
            }

            bool changesScene = facility is "VAB" or "SPH" or "TrackingStation";
            if (changesScene)
            {
                yield return RunSceneChange(
                    $"Entering {facility}",
                    args.TimeoutSeconds,
                    () => ClickFacility(facility),
                    callback
                );
                yield break;
            }

            string error = ClickFacility(facility);
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            // Overlay facilities spawn their UI over the next couple of frames.
            for (int i = 0; i < 5; i++)
                yield return null;

            callback(
                McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["scene"] = HighLogic.LoadedScene.ToString(),
                        ["facility"] = facility,
                        ["note"] =
                            "Facility UI opened. Close it via its UI (e.g. the exit button) to return to the Space Center view.",
                    }
                )
            );
        }

        /// <summary>
        /// Click a KSC building, after checking the conditions under which the
        /// click would only show a "facility locked/closed/damaged" popup.
        /// </summary>
        static string ClickFacility(string facility)
        {
            var game = HighLogic.CurrentGame;
            var p = game.Parameters.SpaceCenter;

            SpaceCenterBuilding building;
            bool allowed;
            switch (facility)
            {
                case "VAB":
                    building = FindBuilding<VehicleAssemblyBuilding>();
                    allowed = p.CanGoInVAB;
                    break;
                case "SPH":
                    building = FindBuilding<SpacePlaneHangarBuilding>();
                    allowed = p.CanGoInSPH;
                    break;
                case "TrackingStation":
                    building = FindBuilding<TrackingStationBuilding>();
                    allowed = p.CanGoInTrackingStation;
                    break;
                case "AstronautComplex":
                    building = FindBuilding<AstronautComplexFacility>();
                    allowed = p.CanGoToAstronautC;
                    break;
                case "Administration":
                    building = FindBuilding<AdministrationFacility>();
                    allowed = p.CanGoToAdmin;
                    break;
                case "MissionControl":
                    building = FindBuilding<MissionControlBuilding>();
                    allowed = p.CanGoToMissionControl;
                    break;
                case "RnD":
                    building = FindBuilding<RnDBuilding>();
                    allowed = p.CanGoToRnD;
                    break;
                default:
                    return $"Unknown facility '{facility}'.";
            }

            if (building == null)
                return $"Could not find the {facility} building in the Space Center scene.";
            if (game.Mode == Game.Modes.SCENARIO_NON_RESUMABLE || !allowed)
                return $"The {facility} is locked in this game.";
            if (!building.IsOpen())
                return $"The {facility} is closed in this game mode.";
            if (building.GetStructureDamage() >= 70f)
                return $"The {facility} is destroyed and must be repaired before it can be entered.";

            building.OnLeftClick();
            return null;
        }

        static SpaceCenterBuilding FindBuilding<T>()
            where T : SpaceCenterBuilding => UnityEngine.Object.FindObjectOfType<T>();

        IEnumerator EnterTrackingStation(
            LeaveFlightParams args,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.SPACECENTER:
                    return RunSceneChange(
                        "Entering the Tracking Station",
                        args.TimeoutSeconds,
                        () => ClickFacility("TrackingStation"),
                        callback
                    );

                case GameScenes.FLIGHT:
                    return RunSceneChange(
                        "Leaving to the Tracking Station",
                        args.TimeoutSeconds,
                        () =>
                        {
                            var p = HighLogic.CurrentGame.Parameters;
                            if (
                                !p.Flight.CanLeaveToTrackingStation
                                || !p.SpaceCenter.CanGoInTrackingStation
                            )
                                return "Leaving flight to the Tracking Station is disabled for this game.";
                            return LeaveFlight(
                                GameScenes.TRACKSTATION,
                                args.DiscardUnsavedProgress ?? false
                            );
                        },
                        callback
                    );

                default:
                    return Error(
                        $"ksp_enter_tracking_station only works in the Space Center or flight scenes (current scene: {HighLogic.LoadedScene}).",
                        callback
                    );
            }
        }

        IEnumerator ReturnToSpaceCenter(
            LeaveFlightParams args,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            var p = HighLogic.CurrentGame?.Parameters;
            switch (HighLogic.LoadedScene)
            {
                case GameScenes.FLIGHT:
                    return RunSceneChange(
                        "Leaving to the Space Center",
                        args.TimeoutSeconds,
                        () =>
                        {
                            if (!p.Flight.CanLeaveToSpaceCenter)
                                return "Leaving flight to the Space Center is disabled for this game.";
                            return LeaveFlight(
                                GameScenes.SPACECENTER,
                                args.DiscardUnsavedProgress ?? false
                            );
                        },
                        callback
                    );

                case GameScenes.EDITOR:
                    return RunSceneChange(
                        "Leaving the editor",
                        args.TimeoutSeconds,
                        () =>
                        {
                            if (!p.Editor.CanLeaveToSpaceCenter)
                                return "Leaving the editor to the Space Center is disabled for this game.";
                            if (EditorLogic.fetch == null)
                                return "The editor is not ready yet.";
                            // The editor exit button's "leave" path.
                            EditorLogic.fetch.onExitConfirm();
                            return null;
                        },
                        callback
                    );

                case GameScenes.TRACKSTATION:
                    return RunSceneChange(
                        "Leaving the Tracking Station",
                        args.TimeoutSeconds,
                        () =>
                        {
                            if (!p.TrackingStation.CanLeaveToSpaceCenter)
                                return "Leaving the Tracking Station to the Space Center is disabled for this game.";
                            // Replicates SpaceTracking.BtnOnClick_LeaveTrackingStation.
                            GamePersistence.SaveGame(
                                "persistent",
                                HighLogic.SaveFolder,
                                SaveMode.OVERWRITE
                            );
                            HighLogic.LoadScene(GameScenes.SPACECENTER);
                            return null;
                        },
                        callback
                    );

                default:
                    return Error(
                        $"ksp_return_to_space_center only works in the flight, editor or tracking station scenes (current scene: {HighLogic.LoadedScene}).",
                        callback
                    );
            }
        }

        /// <summary>
        /// Replicates the pause menu's Space Center / Tracking Station buttons.
        /// </summary>
        static string LeaveFlight(GameScenes scene, bool discardUnsaved)
        {
            if (FlightGlobals.ActiveVessel == null)
                return "The flight scene is not ready yet.";

            var status = FlightGlobals.ClearToSave(logMsg: false);
            if (status == ClearToSaveStatus.CLEAR)
            {
                GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);
                FlightGlobals.ClearpersistentIdDictionaries();
                GamePersistence.SaveGame(
                    HighLogic.CurrentGame.Updated(),
                    "persistent",
                    HighLogic.SaveFolder,
                    SaveMode.OVERWRITE
                );
                HighLogic.LoadScene(scene);
                return null;
            }

            if (!discardUnsaved)
                return $"The game cannot be saved right now ({status}). Wait until the vessel is in a stable state, or pass discardUnsavedProgress=true to leave and lose all progress since the last save.";
            if (!HighLogic.CurrentGame.Parameters.Flight.CanRestart)
                return $"The game cannot be saved right now ({status}), and leaving without saving is disabled for this game.";

            // The "revert to last save and leave" option.
            GameEvents.onSceneConfirmExit.Fire(HighLogic.CurrentGame.startScene);
            HighLogic.LoadScene(scene);
            return null;
        }

        #endregion

        #region Revert

        IEnumerator RevertToLaunch(TimeoutParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            return RunSceneChange(
                "Reverting to launch",
                args.TimeoutSeconds,
                () =>
                {
                    if (!CanRevertToLaunch(out string reason))
                        return reason;
                    FlightDriver.RevertToLaunch();
                    return null;
                },
                callback
            );
        }

        IEnumerator RevertToEditor(TimeoutParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            return RunSceneChange(
                "Reverting to the editor",
                args.TimeoutSeconds,
                () =>
                {
                    if (!CanRevertToEditor(out string reason))
                        return reason;
                    FlightDriver.RevertToPrelaunch(ShipConstruction.ShipType);
                    return null;
                },
                callback
            );
        }

        // Conditions from PauseMenu.drawStockRevertOptions.
        static bool CanRevertToLaunch(out string reason)
        {
            reason = null;
            if (!HighLogic.LoadedSceneIsFlight)
                reason =
                    $"Reverting is only possible in flight (current scene: {HighLogic.LoadedScene}).";
            else if (!HighLogic.CurrentGame.Parameters.Flight.CanRestart)
                reason = "Reverting flights is disabled for this game.";
            else if (!FlightDriver.CanRevertToPostInit || FlightDriver.PostInitState == null)
                reason = "This flight cannot be reverted to launch.";
            return reason == null;
        }

        static bool CanRevertToEditor(out string reason)
        {
            reason = null;
            if (!HighLogic.LoadedSceneIsFlight)
                reason =
                    $"Reverting is only possible in flight (current scene: {HighLogic.LoadedScene}).";
            else if (!HighLogic.CurrentGame.Parameters.Flight.CanLeaveToEditor)
                reason = "Reverting to the editor is disabled for this game.";
            else if (
                !FlightDriver.CanRevertToPrelaunch
                || FlightDriver.PreLaunchState == null
                || ShipConstruction.ShipConfig == null
            )
                reason = "This flight cannot be reverted to the editor.";
            else if (
                ShipConstruction.ShipType != EditorFacility.VAB
                && ShipConstruction.ShipType != EditorFacility.SPH
            )
                reason = $"Unknown editor facility {ShipConstruction.ShipType}.";
            return reason == null;
        }

        #endregion

        #region Vessel Helpers

        struct VesselEntry
        {
            public Vessel Vessel;
            public ProtoVessel ProtoVessel;
            public int Index;
            public string Name;
            public Guid Id;
            public uint PersistentId;
            public bool Owned;

            public JsonObject ToJson()
            {
                if (Vessel != null)
                {
                    var json = VesselSummary(Vessel);
                    json["isActive"] = Vessel == FlightGlobals.ActiveVessel;
                    return json;
                }

                var pv = ProtoVessel;
                string body = null;
                int bodyIndex = pv.orbitSnapShot?.ReferenceBodyIndex ?? -1;
                if (
                    FlightGlobals.fetch != null
                    && bodyIndex >= 0
                    && bodyIndex < FlightGlobals.Bodies.Count
                )
                    body = FlightGlobals.Bodies[bodyIndex].bodyName;

                return new JsonObject
                {
                    ["name"] = Name,
                    ["vesselId"] = Id.ToString(),
                    ["persistentId"] = PersistentId,
                    ["type"] = pv.vesselType.ToString(),
                    ["situation"] = pv.situation.ToString(),
                    ["body"] = body,
                    ["loaded"] = false,
                    ["owned"] = Owned,
                    ["isActive"] = false,
                };
            }
        }

        static JsonObject VesselSummary(Vessel v)
        {
            return new JsonObject
            {
                ["name"] = v.GetDisplayName(),
                ["vesselId"] = v.id.ToString(),
                ["persistentId"] = v.persistentId,
                ["type"] = v.vesselType.ToString(),
                ["situation"] = v.situation.ToString(),
                ["body"] = v.mainBody != null ? v.mainBody.bodyName : null,
                ["loaded"] = v.loaded,
                ["owned"] = v.DiscoveryInfo.Level == DiscoveryLevels.Owned,
            };
        }

        /// <summary>
        /// Vessels of the current game. Scenes with a planetarium have live
        /// Vessel objects; the editor only has the saved flight state.
        /// </summary>
        static List<VesselEntry> GetVesselEntries()
        {
            var entries = new List<VesselEntry>();

            if (HighLogic.LoadedSceneHasPlanetarium && FlightGlobals.fetch != null)
            {
                var vessels = FlightGlobals.Vessels;
                for (int i = 0; i < vessels.Count; i++)
                {
                    var v = vessels[i];
                    if (v == null)
                        continue;
                    entries.Add(
                        new VesselEntry
                        {
                            Vessel = v,
                            ProtoVessel = v.protoVessel,
                            Index = i,
                            Name = v.GetDisplayName(),
                            Id = v.id,
                            PersistentId = v.persistentId,
                            Owned = v.DiscoveryInfo.Level == DiscoveryLevels.Owned,
                        }
                    );
                }
                return entries;
            }

            var protoVessels = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protoVessels == null)
                return entries;

            for (int i = 0; i < protoVessels.Count; i++)
            {
                var pv = protoVessels[i];
                if (pv == null)
                    continue;
                string state = pv.discoveryInfo?.GetValue("state");
                bool owned =
                    state != null
                    && int.TryParse(state, out int level)
                    && (DiscoveryLevels)level == DiscoveryLevels.Owned;
                entries.Add(
                    new VesselEntry
                    {
                        ProtoVessel = pv,
                        Index = i,
                        Name = pv.GetDisplayName(),
                        Id = pv.vesselID,
                        PersistentId = pv.persistentId,
                        Owned = owned,
                    }
                );
            }
            return entries;
        }

        static string FindVessel(SwitchVesselParams args, out VesselEntry entry)
        {
            entry = default;
            var entries = GetVesselEntries();
            List<VesselEntry> matches;

            if (!string.IsNullOrEmpty(args.VesselId))
            {
                Guid id;
                try
                {
                    id = new Guid(args.VesselId);
                }
                catch (FormatException)
                {
                    return $"Invalid vesselId '{args.VesselId}'.";
                }
                matches = entries.Where(e => e.Id == id).ToList();
            }
            else if (args.PersistentId.HasValue)
            {
                matches = entries.Where(e => e.PersistentId == args.PersistentId.Value).ToList();
            }
            else if (!string.IsNullOrEmpty(args.Name))
            {
                matches = entries
                    .Where(e =>
                        string.Equals(e.Name, args.Name, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();
                if (matches.Count > 1)
                {
                    var ids = string.Join(", ", matches.Select(m => m.Id.ToString()));
                    return $"{matches.Count} vessels are named '{args.Name}'. Specify vesselId instead (one of: {ids}).";
                }
            }
            else
            {
                return "Specify one of vesselId, persistentId or name.";
            }

            if (matches.Count == 0)
                return "No matching vessel found. Use ksp_list_vessels to see available vessels.";

            entry = matches[0];
            return null;
        }

        #endregion

        static IEnumerator Error(string message, Action<McpProtocol.ToolCallResult> callback)
        {
            callback(McpProtocol.ToolError(message));
            yield break;
        }
    }
}
