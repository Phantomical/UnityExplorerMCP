using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KSP.Localization;
using KSP.UI.Screens;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Vehicle editor (VAB/SPH) tools. Part placement replicates what
    /// EditorLogic does when the player picks up a part and drops it onto an
    /// attach node or a surface, with the mouse-driven parts (raycasts from the
    /// cursor, attach node icons) replaced by explicit targets. Everything else
    /// (symmetry, undo history, staging, crew, events) goes through the same
    /// EditorLogic methods the editor's own state machine uses.
    ///
    /// Parts are identified by their persistentId. Positions are reported in
    /// editor world axes (+Y is up) relative to the root part.
    /// </summary>
    public class KspEditorTools
    {
        readonly ToolRegistry _tools;
        readonly KspSceneTools _sceneTools;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // States in which nothing is held or being tweaked, so the editor
        // would accept a new pick from the player.
        static readonly string[] IdleStates =
        {
            "st_idle",
            "st_offset_select",
            "st_rotate_select",
            "st_root_unselected",
        };

        // The layer mask EditorLogic uses when moving parts between the
        // "held" and "placed" layers.
        const int PartLayerIgnoreMask = 2097152;

        public KspEditorTools(ToolRegistry tools, KspSceneTools sceneTools)
        {
            _tools = tools;
            _sceneTools = sceneTools;
        }

        #region Parameter Types

        public struct GetShipParams
        {
            [McpParam(
                "Include each part's attach nodes and what is attached to them (default true)."
            )]
            public bool? IncludeNodes { get; set; }

            [McpParam("Include resources (fuel etc.) for each part (default false).")]
            public bool? IncludeResources { get; set; }

            [McpParam("Only describe these parts (persistent ids). Default: all parts.")]
            public long[] PartIds { get; set; }
        }

        public struct ListPartsParams
        {
            [McpParam(
                "Words to search for in the part's name, title, manufacturer and tags (all must match, case-insensitive)."
            )]
            public string Search { get; set; }

            [McpParam(
                "Only parts in this editor category.",
                EnumValues = new[]
                {
                    "Pods",
                    "FuelTank",
                    "Engine",
                    "Control",
                    "Structural",
                    "Coupling",
                    "Payload",
                    "Aero",
                    "Ground",
                    "Thermal",
                    "Electrical",
                    "Communication",
                    "Science",
                    "Cargo",
                    "Robotics",
                    "Utility",
                    "Propulsion",
                }
            )]
            public string Category { get; set; }

            [McpParam("Max results (default 50)")]
            public int? Limit { get; set; }

            [McpParam("Skip first N results (default 0)")]
            public int? Offset { get; set; }
        }

        public struct DescribePartParams
        {
            [McpParam(
                "Part name (the internal name from ksp_editor_list_parts, e.g. 'fuelTankSmallFlat').",
                Required = true
            )]
            public string Part { get; set; }
        }

        public struct SurfaceParams
        {
            [McpParam(
                "Direction around the parent's up (+Y) axis to attach on, in degrees: 0 = the parent's +Z side, 90 = its +X side. Default 0."
            )]
            public float? Angle { get; set; }

            [McpParam(
                "Height along the parent's up axis, in meters from the parent's origin. Default 0."
            )]
            public float? Height { get; set; }

            [McpParam(
                "Instead of angle/height: ray origin in the parent's local coordinates. The part is attached where the ray first hits the parent. Use with direction, e.g. to attach to the top or bottom of a part."
            )]
            public Vec3Param? Origin { get; set; }

            [McpParam("Ray direction in the parent's local coordinates (use with origin).")]
            public Vec3Param? Direction { get; set; }
        }

        public struct AddPartParams
        {
            [McpParam(
                "Part name (the internal name from ksp_editor_list_parts, e.g. 'mk1pod.v2').",
                Required = true
            )]
            public string Part { get; set; }

            [McpParam(
                "Persistent id of the part to attach to. Omit only when the ship is empty, in which case the part becomes the root part."
            )]
            public long? ParentId { get; set; }

            [McpParam(
                "Stack attachment: id of the parent's attach node to attach to (e.g. 'bottom'). Must be free."
            )]
            public string ParentNode { get; set; }

            [McpParam(
                "Stack attachment: id of the new part's attach node to connect (e.g. 'top'). Default: the node facing the parent node."
            )]
            public string Node { get; set; }

            [McpParam(
                "Surface attachment: where on the parent's surface to attach. Use instead of parentNode."
            )]
            public SurfaceParams? Surface { get; set; }

            [McpParam(
                "Total number of copies placed in symmetry (1-8, default 1). The game may change this, e.g. to match a symmetric parent or a multi-coupler."
            )]
            public int? Symmetry { get; set; }

            [McpParam(
                "Symmetry method (default: the editor's current setting). Mirror symmetry places at most 2 copies.",
                EnumValues = new[] { "Radial", "Mirror" }
            )]
            public string SymmetryMethod { get; set; }

            [McpParam(
                "Rotate the part about the attachment axis (the parent node's direction, or the surface normal) by this many degrees. Default 0."
            )]
            public float? Rotation { get; set; }

            [McpParam("Part variant name (see ksp_editor_describe_part). Default: the base variant.")]
            public string Variant { get; set; }
        }

        public struct MovePartParams
        {
            [McpParam("Persistent id of the part to move.", Required = true)]
            public long PartId { get; set; }

            [McpParam("Persistent id of the new parent part.", Required = true)]
            public long ParentId { get; set; }

            [McpParam("Stack attachment: id of the new parent's attach node to attach to.")]
            public string ParentNode { get; set; }

            [McpParam(
                "Stack attachment: id of the moved part's attach node to connect. Default: the free node facing the parent node."
            )]
            public string Node { get; set; }

            [McpParam("Surface attachment: where on the new parent's surface to attach.")]
            public SurfaceParams? Surface { get; set; }

            [McpParam(
                "Total number of copies placed in symmetry (1-8). Default: the part's current symmetry."
            )]
            public int? Symmetry { get; set; }

            [McpParam(
                "Symmetry method. Default: the part's current method.",
                EnumValues = new[] { "Radial", "Mirror" }
            )]
            public string SymmetryMethod { get; set; }

            [McpParam("Rotate the part about the attachment axis by this many degrees.")]
            public float? Rotation { get; set; }
        }

        public struct PartIdParams
        {
            [McpParam("Persistent id of the part (from ksp_editor_get_ship).", Required = true)]
            public long PartId { get; set; }
        }

        public struct TransformPartParams
        {
            [McpParam("Persistent id of the part.", Required = true)]
            public long PartId { get; set; }

            [McpParam("Translate the part by this many meters.")]
            public Vec3Param? Offset { get; set; }

            [McpParam(
                "Rotate the part by these Euler angles in degrees (applied Z, X, Y). Parts rotate around the node attaching them to their parent."
            )]
            public Vec3Param? Rotate { get; set; }

            [McpParam(
                "Axes for offset/rotate: 'world' (editor axes, +Y up; default) or 'local' (the part's own axes).",
                EnumValues = new[] { "world", "local" }
            )]
            public string Space { get; set; }
        }

        public struct FieldChange
        {
            [McpParam("Module name (e.g. 'ModuleEngines'), or 'Part' for part-level fields.")]
            public string Module { get; set; }

            [McpParam(
                "Module index (from ksp_editor_get_part). Needed when the part has several modules with the same name."
            )]
            public int? ModuleIndex { get; set; }

            [McpParam("Field name (from ksp_editor_get_part).")]
            public string Field { get; set; }

            [McpParam(
                "New value as a string, e.g. '75', 'true', or an option / variant name for choice fields."
            )]
            public string Value { get; set; }
        }

        public struct ResourceChange
        {
            [McpParam("Resource name (e.g. 'LiquidFuel').")]
            public string Resource { get; set; }

            [McpParam("New amount, in units.")]
            public double? Amount { get; set; }

            [McpParam("New amount as a fraction of capacity, 0-1. Use instead of amount.")]
            public double? Fraction { get; set; }

            [McpParam("Whether the resource can flow out of the part (the flow toggle).")]
            public bool? Flow { get; set; }
        }

        public struct EventCall
        {
            [McpParam("Module name, or 'Part' for part-level events.")]
            public string Module { get; set; }

            [McpParam("Module index, if several modules share the name.")]
            public int? ModuleIndex { get; set; }

            [McpParam("Event (button) name from ksp_editor_get_part.")]
            public string Event { get; set; }
        }

        public struct ActionChange
        {
            [McpParam("Module name, or 'Part' for part-level actions.")]
            public string Module { get; set; }

            [McpParam("Module index, if several modules share the name.")]
            public int? ModuleIndex { get; set; }

            [McpParam("Action name from ksp_editor_get_part.")]
            public string Action { get; set; }

            [McpParam(
                "Action groups to assign the action to, replacing its current groups: Stage, Gear, Light, RCS, SAS, Brakes, Abort, Custom01-Custom10. Empty to clear."
            )]
            public string[] Groups { get; set; }
        }

        public struct ModifyPartParams
        {
            [McpParam("Persistent id of the part.", Required = true)]
            public long PartId { get; set; }

            [McpParam("Part action window fields (tweakables) to set.")]
            public FieldChange[] Fields { get; set; }

            [McpParam("Resource amounts / flow states to set.")]
            public ResourceChange[] Resources { get; set; }

            [McpParam("Part action window buttons to press, in order, after setting fields.")]
            public EventCall[] Events { get; set; }

            [McpParam("Action group assignments to set.")]
            public ActionChange[] Actions { get; set; }

            [McpParam(
                "Also apply field, resource and action group changes to the part's symmetry counterparts, like the editor does (default true)."
            )]
            public bool? ApplySymmetry { get; set; }
        }

        public struct SetStageParams
        {
            [McpParam(
                "Persistent id of a part with a staging icon. Its whole symmetry group moves with it.",
                Required = true
            )]
            public long PartId { get; set; }

            [McpParam(
                "Stage number to move the part to. Stages fire from the highest number down to 0.",
                Required = true
            )]
            public int Stage { get; set; }

            [McpParam(
                "Insert a new empty stage with this number first (later stages are renumbered up), then move the part into it. Default false."
            )]
            public bool? NewStage { get; set; }
        }

        public struct UndoParams
        {
            [McpParam("Number of steps (default 1).")]
            public int? Steps { get; set; }

            [McpParam("Redo instead of undo (default false).")]
            public bool? Redo { get; set; }
        }

        public struct NewShipParams
        {
            [McpParam(
                "Discard unsaved changes to the current ship. Without this the tool fails if there are unsaved changes."
            )]
            public bool? DiscardChanges { get; set; }
        }

        public struct ListCraftParams
        {
            [McpParam(
                "Only craft built in this facility. Default: both.",
                EnumValues = new[] { "VAB", "SPH" }
            )]
            public string Facility { get; set; }

            [McpParam("Include the stock example craft, if this game allows them (default true).")]
            public bool? IncludeStock { get; set; }
        }

        public struct LoadCraftParams
        {
            [McpParam("Craft file path, as returned by ksp_editor_list_craft.", Required = true)]
            public string Path { get; set; }

            [McpParam(
                "Discard unsaved changes to the current ship. Without this the tool fails if there are unsaved changes."
            )]
            public bool? DiscardChanges { get; set; }
        }

        public struct SaveCraftParams
        {
            [McpParam("Ship name to save as. Default: the current ship name.")]
            public string Name { get; set; }

            [McpParam("Ship description. Default: unchanged.")]
            public string Description { get; set; }

            [McpParam("Overwrite an existing craft file with the same name (default false).")]
            public bool? Overwrite { get; set; }
        }

        public struct SetShipInfoParams
        {
            [McpParam("New ship name.")]
            public string Name { get; set; }

            [McpParam("New ship description.")]
            public string Description { get; set; }
        }

        public struct LaunchParams
        {
            [McpParam(
                "Launch site name (see launchSites in ksp_editor_get_ship). Default: the editor's selected site."
            )]
            public string LaunchSite { get; set; }

            [McpParam("Max seconds to wait for the flight scene to finish loading (default 180).")]
            public float? TimeoutSeconds { get; set; }
        }

        /// <summary>Where and how to attach a part; shared by add and move.</summary>
        struct PlacementSpec
        {
            public string ParentNode;
            public string Node;
            public SurfaceParams? Surface;
            public int Symmetry;
            public SymmetryMethod Method;
            public float Rotation;
        }

        #endregion

        public void Register()
        {
            _tools.Register<GetShipParams>(
                "ksp_editor_get_ship",
                "Editor (VAB/SPH) only. Describe the ship being built: name, mass, cost, delta-v, staging, launch sites, undo state, and every part with its persistent id, parent, attachment, symmetry, position and free attach nodes.",
                GetShip
            );

            _tools.Register<ListPartsParams>(
                "ksp_editor_list_parts",
                "Editor only. Search the parts that are available in the editor's part list (researched and purchased). Returns internal part names for ksp_editor_add_part.",
                ListParts
            );

            _tools.Register<DescribePartParams>(
                "ksp_editor_describe_part",
                "Editor only. Detailed information about an available part type: attach nodes (positions and directions), attachment rules, modules, resources, variants and crew capacity.",
                DescribePart
            );

            _tools.Register<PartIdParams>(
                "ksp_editor_get_part",
                "Editor only. Detailed state of one part on the ship: attach nodes, its part action window fields (tweakables) with their current values and allowed ranges, buttons, action groups and resources.",
                GetPart
            );

            _tools.RegisterCoroutine<AddPartParams>(
                "ksp_editor_add_part",
                "Editor only. Add a part to the ship, like dragging it from the parts list and dropping it on another part. Attach it either to a free attach node of the parent (parentNode, optionally node) or to the parent's surface (surface). The first part of an empty ship becomes the root part. Supports radial/mirror symmetry.",
                AddPart
            );

            _tools.RegisterCoroutine<MovePartParams>(
                "ksp_editor_move_part",
                "Editor only. Detach a part (with everything attached to it) and re-attach it somewhere else on the ship, like picking it up and dropping it. If the part has symmetry counterparts they are removed and recreated according to the symmetry parameter. The root part cannot be moved.",
                MovePart
            );

            _tools.Register<PartIdParams>(
                "ksp_editor_remove_part",
                "Editor only. Delete a part, everything attached to it, and its symmetry counterparts. The root part cannot be removed; use ksp_editor_new_ship to start over.",
                RemovePart
            );

            _tools.Register<TransformPartParams>(
                "ksp_editor_transform_part",
                "Editor only. Offset and/or rotate a placed part, like the offset and rotate gizmos (symmetry counterparts follow). As with the gizmo, offsets are limited so the part stays touching its parent.",
                TransformPart
            );

            _tools.Register<ModifyPartParams>(
                "ksp_editor_modify_part",
                "Editor only. Change a part's settings like the part action window and action group editor do: set tweakable fields (thrust limit, deploy settings, variants, ...), resource amounts, press buttons, and assign action groups.",
                ModifyPart
            );

            _tools.RegisterCoroutine<SetStageParams>(
                "ksp_editor_set_stage",
                "Editor only. Move a part's staging icon to another stage, like dragging it in the staging list. Returns the new staging sequence.",
                SetStage
            );

            _tools.RegisterCoroutine<UndoParams>(
                "ksp_editor_undo",
                "Editor only. Undo (or redo) ship construction steps, like Ctrl+Z / Ctrl+Y. This reloads the ship, so part ids change: call ksp_editor_get_ship afterwards.",
                Undo
            );

            _tools.RegisterCoroutine<NewShipParams>(
                "ksp_editor_new_ship",
                "Editor only. Clear the editor and start a new ship, like the New button.",
                NewShip
            );

            _tools.Register<ListCraftParams>(
                "ksp_editor_list_craft",
                "Editor only. List saved craft files that ksp_editor_load_craft can open.",
                ListCraft
            );

            _tools.RegisterCoroutine<LoadCraftParams>(
                "ksp_editor_load_craft",
                "Editor only. Load a saved craft, replacing the current ship, like the Load button.",
                LoadCraft
            );

            _tools.Register<SaveCraftParams>(
                "ksp_editor_save_craft",
                "Editor only. Save the ship to a craft file, like the Save button. Optionally rename it first.",
                SaveCraft
            );

            _tools.Register<SetShipInfoParams>(
                "ksp_editor_set_ship_info",
                "Editor only. Set the ship's name and/or description without saving.",
                SetShipInfo
            );

            _tools.RegisterCoroutine<LaunchParams>(
                "ksp_editor_launch",
                "Editor only. Launch the ship, like the Launch button, and wait for the flight scene to load. Fails if the game's pre-flight checks show a dialog (e.g. the launch site is obstructed or the ship exceeds the facility's limits).",
                Launch
            );
        }

        #region Editor Helpers

        /// <summary>
        /// Check that the editor is up and not in the middle of a player action
        /// (holding a part, dragging a gizmo) that a programmatic change would
        /// interfere with.
        /// </summary>
        static string CheckEditor(out EditorLogic editor, bool requireIdle = true)
        {
            editor = EditorLogic.fetch;
            if (!HighLogic.LoadedSceneIsEditor || editor == null)
                return $"Not in the VAB or SPH (current scene: {HighLogic.LoadedScene}). Use ksp_enter_facility to enter one.";
            if (!editor.FSMStarted || editor.ship == null)
                return "The editor is not ready yet.";
            if (!requireIdle)
                return null;

            if (editor.selectedPart != null)
                return $"The player is currently holding or tweaking a part ({editor.selectedPart.partInfo?.title}). Drop it or finish the action first.";

            string state = editor.currentStateName;
            bool emptyShip = editor.ship.parts.Count == 0;
            if (!IdleStates.Contains(state) && !(state == "st_podSelect" && emptyShip))
                return $"The editor is busy (state {state}). Finish the current action first.";
            return null;
        }

        static string FindShipPart(EditorLogic editor, long id, out Part part)
        {
            part = editor.ship.parts.FirstOrDefault(p => p != null && p.persistentId == id);
            if (part == null)
                return $"No part with id {id} on the ship. Use ksp_editor_get_ship to see part ids.";
            return null;
        }

        /// <summary>Parts the editor's part list shows, with the same filters.</summary>
        static List<AvailablePart> AvailableParts()
        {
            var list = PartLoader.LoadedPartsList;
            var partList = EditorPartList.Instance;
            if (partList != null)
            {
                list = partList.ExcludeFilters.GetFilteredList(list);
                EditorPartListFilter<AvailablePart>.FilterList(
                    list,
                    partList.AmountAvailableFilter.FilterCriteria
                );
            }
            return list.Where(ap =>
                    ap.partPrefab != null && ap.category != PartCategories.none && !ap.TechHidden
                )
                .ToList();
        }

        static string FindAvailablePart(string name, out AvailablePart part)
        {
            part = null;
            if (string.IsNullOrEmpty(name))
                return "Part name is missing.";

            var ap = PartLoader.getPartInfoByName(name.Replace('_', '.'));
            if (ap == null)
            {
                // Titles are what players see; accept an unambiguous one.
                var byTitle = PartLoader
                    .LoadedPartsList.Where(p =>
                        string.Equals(p.title, name, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();
                if (byTitle.Count == 1)
                    ap = byTitle[0];
            }
            if (ap == null)
                return $"Unknown part '{name}'. Use ksp_editor_list_parts to find part names.";
            if (!AvailableParts().Contains(ap))
                return $"Part '{ap.name}' ({ap.title}) is not available in this game (not researched or purchased).";
            part = ap;
            return null;
        }

        // EditorLogic.rootPartsOnlyFilter.
        static bool CanBeRoot(AvailablePart ap)
        {
            var p = ap.partPrefab;
            return p.attachRules.allowStack
                && p.attachRules.allowRoot
                && p.physicalSignificance == Part.PhysicalSignificance.FULL
                && p.FindModuleImplementing<ModuleGroundPart>() == null;
        }

        static bool HasUnsavedChanges(EditorLogic editor) =>
            editor.undoIndexAtLastSave != editor.undoLevel && editor.ship.parts.Count > 0;

        static bool InSubtree(Part p, Part ancestor)
        {
            for (var x = p; x != null; x = x.parent)
            {
                if (x == ancestor)
                    return true;
            }
            return false;
        }

        static string Title(Part p) => p.partInfo != null ? p.partInfo.title : p.name;

        static string Describe(Part p) => $"{Title(p)} (id {p.persistentId})";

        static string ParseSymmetryMethod(string value, SymmetryMethod fallback, out SymmetryMethod method)
        {
            method = fallback;
            if (string.IsNullOrEmpty(value))
                return null;
            if (string.Equals(value, "Radial", StringComparison.OrdinalIgnoreCase))
                method = SymmetryMethod.Radial;
            else if (string.Equals(value, "Mirror", StringComparison.OrdinalIgnoreCase))
                method = SymmetryMethod.Mirror;
            else
                return $"Unknown symmetry method '{value}'. Use Radial or Mirror.";
            return null;
        }

        #endregion

        #region Ship Info

        McpProtocol.ToolCallResult GetShip(GetShipParams args)
        {
            string error = CheckEditor(out var editor, requireIdle: false);
            if (error != null)
                return McpProtocol.ToolError(error);

            var result = ShipSummary(editor);
            var parts = new JsonArray();
            var stages = StageNumbers();
            HashSet<long> filter = args.PartIds != null ? new HashSet<long>(args.PartIds) : null;
            foreach (var p in editor.ship.parts)
            {
                if (p == null || (filter != null && !filter.Contains(p.persistentId)))
                    continue;
                parts.Add(
                    PartJson(
                        p,
                        editor,
                        stages,
                        args.IncludeNodes ?? true,
                        args.IncludeResources ?? false
                    )
                );
            }
            result["staging"] = StagingJson();
            result["parts"] = parts;
            return McpProtocol.ToolSuccess(result);
        }

        static JsonObject ShipSummary(EditorLogic editor)
        {
            var ship = editor.ship;
            float mass = ship.GetShipMass(out float dryMass, out float fuelMass);
            float cost = ship.GetShipCosts(out float dryCost, out float fuelCost);

            var result = new JsonObject
            {
                ["facility"] = EditorDriver.editorFacility.ToString(),
                ["name"] = editor.shipNameField.text,
                ["description"] = editor.shipDescriptionField.text,
                ["partCount"] = ship.parts.Count,
                ["rootPartId"] = editor.rootPart != null && ship.parts.Count > 0
                    ? editor.rootPart.persistentId
                    : null,
                ["mass"] = new JsonObject
                {
                    ["total"] = R(mass),
                    ["dry"] = R(dryMass),
                    ["resources"] = R(fuelMass),
                },
                ["cost"] = new JsonObject
                {
                    ["total"] = R(cost, 0),
                    ["dry"] = R(dryCost, 0),
                    ["resources"] = R(fuelCost, 0),
                },
                ["editorState"] = editor.currentStateName,
                ["hasUnsavedChanges"] = HasUnsavedChanges(editor),
                ["canUndo"] = editor.undoLevel > 1,
                ["canRedo"] = editor.undoLevel < ShipConstruction.backups.Count,
                ["launchSite"] = editor.launchSiteName,
                ["launchSites"] = new JsonArray(
                    EditorDriver.ValidLaunchSites.Select(s => (JsonNode)s).ToArray()
                ),
            };

            if (ship.parts.Count > 0)
                result["size"] = Vec(ShipConstruction.CalculateCraftSize(ship));

            var dv = ship.vesselDeltaV;
            if (dv != null && dv.IsReady)
            {
                var dvStages = new JsonArray();
                foreach (var s in dv.OperatingStageInfo.OrderByDescending(s => s.stage))
                {
                    dvStages.Add(
                        new JsonObject
                        {
                            ["stage"] = s.stage,
                            ["deltaVVac"] = R(s.deltaVinVac, 1),
                            ["deltaVASL"] = R(s.deltaVatASL, 1),
                            ["twrVac"] = R(s.TWRVac, 2),
                            ["twrASL"] = R(s.TWRASL, 2),
                            ["burnTime"] = R(s.stageBurnTime, 1),
                            ["startMass"] = R(s.startMass),
                            ["endMass"] = R(s.endMass),
                        }
                    );
                }
                result["deltaV"] = new JsonObject
                {
                    ["totalVac"] = R(dv.TotalDeltaVVac, 1),
                    ["totalASL"] = R(dv.TotalDeltaVASL, 1),
                    ["stages"] = dvStages,
                };
            }

            return result;
        }

        static JsonObject PartJson(
            Part p,
            EditorLogic editor,
            Dictionary<Part, int> stages,
            bool includeNodes,
            bool includeResources
        )
        {
            var root = editor.rootPart != null ? editor.rootPart.transform.position : Vector3.zero;
            var json = new JsonObject
            {
                ["id"] = p.persistentId,
                ["name"] = p.partInfo?.name,
                ["title"] = Title(p),
            };

            if (p.parent != null)
            {
                json["parentId"] = p.parent.persistentId;
                if (p.srfAttachNode != null && p.srfAttachNode.attachedPart == p.parent)
                {
                    json["attachment"] = "surface";
                }
                else
                {
                    json["attachment"] = "stack";
                    json["node"] = p.FindAttachNodeByPart(p.parent)?.id;
                    json["parentNode"] = p.parent.FindAttachNodeByPart(p)?.id;
                }
            }
            else if (p == editor.rootPart)
            {
                json["isRoot"] = true;
            }

            if (p.symmetryCounterparts.Count > 0)
            {
                json["symmetryMethod"] = p.symMethod.ToString();
                json["symmetryCounterparts"] = new JsonArray(
                    p.symmetryCounterparts.Where(c => c != null)
                        .Select(c => (JsonNode)c.persistentId)
                        .ToArray()
                );
            }

            if (stages.TryGetValue(p, out int stage))
                json["stage"] = stage;

            json["position"] = Vec(p.transform.position - root);
            json["up"] = Vec(p.transform.up, 3);
            json["mass"] = R(p.mass + p.GetResourceMass(), 4);
            if (p.CrewCapacity > 0)
                json["crewCapacity"] = p.CrewCapacity;

            if (includeNodes)
            {
                var nodes = new JsonArray();
                foreach (var n in p.attachNodes)
                {
                    nodes.Add(
                        new JsonObject
                        {
                            ["id"] = n.id,
                            ["size"] = n.size,
                            ["attachedId"] = n.attachedPart != null
                                ? n.attachedPart.persistentId
                                : null,
                        }
                    );
                }
                json["nodes"] = nodes;
                json["allowsSurfaceAttach"] = p.attachRules.allowSrfAttach;
            }

            if (includeResources && p.Resources.Count > 0)
                json["resources"] = ResourcesJson(p);

            return json;
        }

        static JsonArray ResourcesJson(Part p)
        {
            var arr = new JsonArray();
            foreach (var r in p.Resources)
            {
                arr.Add(
                    new JsonObject
                    {
                        ["name"] = r.resourceName,
                        ["amount"] = R(r.amount, 2),
                        ["maxAmount"] = R(r.maxAmount, 2),
                        ["flow"] = r.flowState,
                        ["tweakable"] = r.isTweakable,
                    }
                );
            }
            return arr;
        }

        /// <summary>Stage number of every part that has a staging icon.</summary>
        static Dictionary<Part, int> StageNumbers()
        {
            var map = new Dictionary<Part, int>();
            var sm = StageManager.Instance;
            if (sm == null)
                return map;
            for (int i = 0; i < sm.Stages.Count; i++)
            {
                foreach (var part in StageParts(sm.Stages[i]))
                    map[part] = i;
            }
            return map;
        }

        static IEnumerable<Part> StageParts(StageGroup group)
        {
            var seen = new HashSet<Part>();
            foreach (var icon in group.Icons)
            {
                if (icon == null)
                    continue;
                if (icon.Part != null && seen.Add(icon.Part))
                    yield return icon.Part;
                foreach (var grouped in icon.groupedIcons)
                {
                    if (grouped != null && grouped.Part != null && seen.Add(grouped.Part))
                        yield return grouped.Part;
                }
            }
        }

        static JsonArray StagingJson()
        {
            var arr = new JsonArray();
            var sm = StageManager.Instance;
            if (sm == null)
                return arr;
            // Firing order: highest stage first.
            for (int i = sm.Stages.Count - 1; i >= 0; i--)
            {
                var parts = new JsonArray();
                foreach (var p in StageParts(sm.Stages[i]))
                    parts.Add(new JsonObject { ["id"] = p.persistentId, ["title"] = Title(p) });
                arr.Add(new JsonObject { ["stage"] = i, ["parts"] = parts });
            }
            return arr;
        }

        #endregion

        #region Part Catalog

        McpProtocol.ToolCallResult ListParts(ListPartsParams args)
        {
            string error = CheckEditor(out _, requireIdle: false);
            if (error != null)
                return McpProtocol.ToolError(error);

            PartCategories? category = null;
            if (!string.IsNullOrEmpty(args.Category))
            {
                try
                {
                    category = (PartCategories)Enum.Parse(typeof(PartCategories), args.Category, true);
                }
                catch (ArgumentException)
                {
                    return McpProtocol.ToolError($"Unknown category '{args.Category}'.");
                }
            }

            string[] words = (args.Search ?? "")
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant())
                .ToArray();

            var matches = AvailableParts()
                .Where(ap => category == null || ap.category == category)
                .Where(ap =>
                {
                    if (words.Length == 0)
                        return true;
                    string haystack = string.Join(
                            " ",
                            ap.name,
                            ap.title,
                            ap.manufacturer,
                            ap.tags
                        )
                        .ToLowerInvariant();
                    return words.All(haystack.Contains);
                })
                .OrderBy(ap => ap.category)
                .ThenBy(ap => ap.title)
                .ToList();

            var parts = new JsonArray();
            foreach (var ap in matches.Skip(args.Offset ?? 0).Take(args.Limit ?? 50))
            {
                var prefab = ap.partPrefab;
                var entry = new JsonObject
                {
                    ["name"] = ap.name,
                    ["title"] = ap.title,
                    ["category"] = ap.category.ToString(),
                    ["mass"] = R(prefab.mass + prefab.GetResourceMass(), 4),
                    ["cost"] = R(ap.cost, 0),
                    ["profiles"] = ap.bulkheadProfiles,
                    ["nodes"] = new JsonArray(
                        prefab
                            .attachNodes.Select(n => (JsonNode)$"{n.id} (size {n.size})")
                            .ToArray()
                    ),
                    ["surfaceAttach"] = prefab.attachRules.srfAttach,
                };
                if (prefab.CrewCapacity > 0)
                    entry["crewCapacity"] = prefab.CrewCapacity;
                if (prefab.Resources.Count > 0)
                    entry["resources"] = new JsonArray(
                        prefab.Resources.Select(r => (JsonNode)r.resourceName).ToArray()
                    );
                parts.Add(entry);
            }

            return McpProtocol.ToolSuccess(
                new JsonObject { ["totalCount"] = matches.Count, ["parts"] = parts }
            );
        }

        McpProtocol.ToolCallResult DescribePart(DescribePartParams args)
        {
            string error = CheckEditor(out _, requireIdle: false);
            AvailablePart ap = null;
            if (error == null)
                error = FindAvailablePart(args.Part, out ap);
            if (error != null)
                return McpProtocol.ToolError(error);
            return McpProtocol.ToolSuccess(DescribeAvailablePart(ap));
        }

        static JsonObject DescribeAvailablePart(AvailablePart ap)
        {
            var prefab = ap.partPrefab;
            var rules = prefab.attachRules;

            var nodes = new JsonArray();
            foreach (var n in prefab.attachNodes)
                nodes.Add(NodeJson(n, Vector3.zero, null));

            var modules = new JsonArray();
            if (ap.moduleInfos != null)
            {
                foreach (var m in ap.moduleInfos)
                {
                    modules.Add(
                        new JsonObject
                        {
                            ["name"] = m.moduleName,
                            ["info"] = Truncate(m.info, 400),
                        }
                    );
                }
            }

            var json = new JsonObject
            {
                ["name"] = ap.name,
                ["title"] = ap.title,
                ["category"] = ap.category.ToString(),
                ["manufacturer"] = ap.manufacturer,
                ["description"] = Truncate(ap.description, 600),
                ["techRequired"] = ap.TechRequired,
                ["cost"] = R(ap.cost, 0),
                ["dryMass"] = R(prefab.mass, 4),
                ["wetMass"] = R(prefab.mass + prefab.GetResourceMass(), 4),
                ["profiles"] = ap.bulkheadProfiles,
                ["crewCapacity"] = prefab.CrewCapacity,
                ["canBeRoot"] = CanBeRoot(ap),
                ["attachRules"] = new JsonObject
                {
                    ["stack"] = rules.stack,
                    ["surfaceAttach"] = rules.srfAttach,
                    ["allowStack"] = rules.allowStack,
                    ["allowSurfaceAttach"] = rules.allowSrfAttach,
                    ["allowCollision"] = rules.allowCollision,
                },
                ["nodes"] = nodes,
                ["modules"] = modules,
            };

            if (prefab.srfAttachNode != null && rules.srfAttach)
                json["surfaceAttachNode"] = NodeJson(prefab.srfAttachNode, Vector3.zero, null);

            if (prefab.Resources.Count > 0)
                json["resources"] = ResourcesJson(prefab);

            if (prefab.variants != null && prefab.variants.variantList.Count > 0)
            {
                var variants = new JsonArray();
                foreach (var v in prefab.variants.variantList)
                {
                    variants.Add(
                        new JsonObject
                        {
                            ["name"] = v.Name,
                            ["displayName"] = Localizer.Format(v.DisplayName),
                            ["sizeGroup"] = v.SizeGroup,
                        }
                    );
                }
                json["variants"] = variants;
            }

            return json;
        }

        /// <summary>
        /// An attach node, with its position relative to the root part and
        /// direction in editor axes for placed parts, or in the part's own
        /// coordinates for prefabs (owner null).
        /// </summary>
        static JsonObject NodeJson(AttachNode n, Vector3 origin, Part owner)
        {
            var pos = owner != null ? owner.transform.TransformPoint(n.position) - origin : n.position;
            var dir = owner != null ? owner.transform.TransformDirection(n.orientation) : n.orientation;
            return new JsonObject
            {
                ["id"] = n.id,
                ["type"] = n.nodeType.ToString(),
                ["size"] = n.size,
                ["position"] = Vec(pos),
                ["direction"] = Vec(dir.normalized),
                ["attachedId"] = n.attachedPart != null ? n.attachedPart.persistentId : null,
            };
        }

        #endregion

        #region Part Placement

        IEnumerator AddPart(AddPartParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            var result = BeginAddPart(args, out var placement);
            if (placement != null)
            {
                for (int i = 0; i < StartWaitFrames && !AllStarted(placement.Placed); i++)
                    yield return null;
                result = FinishPlacement(placement);
            }
            callback(result);
        }

        IEnumerator MovePart(MovePartParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            var result = BeginMovePart(args, out var placement);
            if (placement != null)
            {
                for (int i = 0; i < StartWaitFrames && !AllStarted(placement.Placed); i++)
                    yield return null;
                result = FinishPlacement(placement);
            }
            callback(result);
        }

        /// <summary>A part that has been attached, with its symmetry counterparts.</summary>
        sealed class Placement
        {
            public EditorLogic Editor;
            public Part Part;
            public PlacementSpec Spec;
            public List<Part> Placed;
        }

        const int StartWaitFrames = 30;

        static bool AllStarted(List<Part> parts) => parts.All(p => p == null || p.started);

        /// <summary>
        /// Parts create their staging icon in Part.Start, which runs after they are created. The
        /// editor sorts held icons into stages when a part is placed, so the icons of parts created
        /// and placed in the same frame are never staged. Sort again once the parts have started.
        /// </summary>
        static McpProtocol.ToolCallResult FinishPlacement(Placement p)
        {
            if (EditorLogic.fetch != p.Editor || p.Part == null)
                return McpProtocol.ToolError("The editor closed before the part was placed.");
            StageManager.Instance?.OnEditorPartPlaced(p.Part);
            return McpProtocol.ToolSuccess(PlacementResult(p.Editor, p.Part, p.Spec, p.Placed));
        }

        McpProtocol.ToolCallResult BeginAddPart(AddPartParams args, out Placement placement)
        {
            placement = null;
            string error = CheckEditor(out var editor);
            AvailablePart ap = null;
            if (error == null)
                error = FindAvailablePart(args.Part, out ap);
            if (error != null)
                return McpProtocol.ToolError(error);

            if (ap.partPrefab is CompoundPart)
                return McpProtocol.ToolError(
                    $"{ap.title} is a two-point part (strut/fuel line), which is not supported."
                );

            string variant = null;
            if (!string.IsNullOrEmpty(args.Variant))
            {
                var variants = ap.partPrefab.variants;
                variant = variants?.variantList.FirstOrDefault(v =>
                        string.Equals(v.Name, args.Variant, StringComparison.OrdinalIgnoreCase)
                    )
                    ?.Name;
                if (variant == null)
                {
                    string names =
                        variants != null
                            ? string.Join(", ", variants.GetVariantNames())
                            : "(none)";
                    return McpProtocol.ToolError(
                        $"{ap.title} has no variant '{args.Variant}'. Variants: {names}."
                    );
                }
            }

            if (editor.ship.parts.Count == 0)
                return AddRootPart(editor, ap, variant, args);

            if (!args.ParentId.HasValue)
                return McpProtocol.ToolError(
                    "parentId is required: the ship already has parts, so the new part must be attached to one of them."
                );
            error = FindShipPart(editor, args.ParentId.Value, out var parent);
            if (error != null)
                return McpProtocol.ToolError(error);

            error = ParseSymmetryMethod(args.SymmetryMethod, editor.symmetryMethod, out var method);
            if (error != null)
                return McpProtocol.ToolError(error);

            var spec = new PlacementSpec
            {
                ParentNode = args.ParentNode,
                Node = args.Node,
                Surface = args.Surface,
                Symmetry = args.Symmetry ?? 1,
                Method = method,
                Rotation = args.Rotation ?? 0f,
            };
            error = CheckSymmetry(spec);
            if (error != null)
                return McpProtocol.ToolError(error);

            var part = CreatePart(editor, ap, variant);
            error = ComputeAttachment(editor, part, parent, spec, moving: false, out var attach);
            if (error != null)
            {
                part.OnDelete();
                UnityEngine.Object.Destroy(part.gameObject);
                return McpProtocol.ToolError(error);
            }

            placement = new Placement
            {
                Editor = editor,
                Part = part,
                Spec = spec,
                Placed = AttachWithSymmetry(editor, part, attach, spec),
            };
            return null;
        }

        McpProtocol.ToolCallResult AddRootPart(
            EditorLogic editor,
            AvailablePart ap,
            string variant,
            AddPartParams args
        )
        {
            if (args.ParentId.HasValue)
                return McpProtocol.ToolError(
                    "The ship is empty, so the first part becomes the root part. Omit parentId."
                );
            if (!CanBeRoot(ap))
                return McpProtocol.ToolError(
                    $"{ap.title} cannot be the root part of a ship. Start with a part that can (e.g. a command pod); see canBeRoot in ksp_editor_describe_part."
                );
            if (editor.currentStateName != "st_podSelect")
                return McpProtocol.ToolError(
                    $"The editor is not waiting for a root part (state {editor.currentStateName})."
                );

            // The same path as clicking a part in the list with no ship.
            editor.OnPodSpawn(ap);
            var root = editor.rootPart;
            if (root == null || !editor.ship.parts.Contains(root))
                return McpProtocol.ToolError("The editor did not accept the root part.");

            var result = new JsonObject();
            if (variant != null && root.variants != null)
            {
                string error = SetVariant(editor, root, variant, applySymmetry: false);
                if (error != null)
                    result["variantError"] = error;
            }

            result["part"] = PartJson(root, editor, StageNumbers(), true, false);
            result["note"] = "The part was placed as the ship's root part.";
            return McpProtocol.ToolSuccess(result);
        }

        /// <summary>Replicates EditorLogic.SpawnPart, without selecting the part.</summary>
        static Part CreatePart(EditorLogic editor, AvailablePart ap, string variant)
        {
            var part = UnityEngine.Object.Instantiate(ap.partPrefab);
            part.gameObject.SetActive(true);
            part.name = ap.name;
            part.persistentId = FlightGlobals.CheckPartpersistentId(
                part.persistentId,
                part,
                removeOldId: false,
                addNewId: true
            );
            variant ??= ap.variant?.Name;
            if (part.variants != null && variant != null)
                part.variants.SetVariant(variant);
            part.InitializeModules();
            part.FindModuleImplementing<ModuleInventoryPart>()?.SetInventoryDefaults();

            part.transform.rotation =
                editor.rootPart != null ? editor.rootPart.transform.rotation : part.initRotation;
            part.attRotation = Quaternion.identity;
            part.attRotation0 = part.transform.localRotation;
            part.attPos0 = part.transform.localPosition;
            part.gameObject.SetLayerRecursive(1, filterTranslucent: true, PartLayerIgnoreMask);
            part.partInfo = ap;

            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartCreated, part);
            return part;
        }

        McpProtocol.ToolCallResult BeginMovePart(MovePartParams args, out Placement placement)
        {
            placement = null;
            string error = CheckEditor(out var editor);
            Part part = null,
                parent = null;
            if (error == null)
                error = FindShipPart(editor, args.PartId, out part);
            if (error == null)
                error = FindShipPart(editor, args.ParentId, out parent);
            if (error != null)
                return McpProtocol.ToolError(error);

            if (part == editor.rootPart)
                return McpProtocol.ToolError(
                    "The root part cannot be moved. Move the other parts instead."
                );

            var moving = new List<Part> { part };
            moving.AddRange(part.symmetryCounterparts.Where(c => c != null));
            var inside = moving.FirstOrDefault(m => InSubtree(parent, m));
            if (inside != null)
                return McpProtocol.ToolError(
                    $"{Describe(parent)} is part of what is being moved (it is attached to {Describe(inside)}). Choose a parent outside the moved parts."
                );

            error = ParseSymmetryMethod(args.SymmetryMethod, part.symMethod, out var method);
            if (error != null)
                return McpProtocol.ToolError(error);

            var spec = new PlacementSpec
            {
                ParentNode = args.ParentNode,
                Node = args.Node,
                Surface = args.Surface,
                Symmetry = args.Symmetry ?? part.symmetryCounterparts.Count + 1,
                Method = method,
                Rotation = args.Rotation ?? 0f,
            };
            error = CheckSymmetry(spec);
            if (error != null)
                return McpProtocol.ToolError(error);

            // Work out the new attachment while still attached, so a bad
            // request leaves the ship untouched.
            var t = part.transform;
            var oldPosition = t.position;
            var oldRotation = t.rotation;
            var oldAttRotation = part.attRotation;
            var oldAttachMode = part.attachMode;
            part.attRotation = Quaternion.identity;
            error = ComputeAttachment(editor, part, parent, spec, moving: true, out var attach);
            if (error != null)
            {
                t.SetPositionAndRotation(oldPosition, oldRotation);
                part.attRotation = oldAttRotation;
                part.attachMode = oldAttachMode;
                return McpProtocol.ToolError(error);
            }

            // Pick the part up: replicates the on_partPicked event.
            editor.selectedPart = part;
            try
            {
                editor.detachPart(part);
                editor.deleteSymmetryParts();
                GameEvents.onEditorPartPicked.Fire(part);
                editor.SetBackup();
                if (part.CrewCapacity > 0)
                    editor.RefreshCrewAssignment(
                        ShipConstruction.ShipConfig,
                        editor.GetPartExistsFilter()
                    );
                GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartDetached, part);
            }
            finally
            {
                editor.selectedPart = null;
            }

            // detachPart clears potentialParent, which createSymmetry needs.
            part.potentialParent = parent;

            placement = new Placement
            {
                Editor = editor,
                Part = part,
                Spec = spec,
                Placed = AttachWithSymmetry(editor, part, attach, spec),
            };
            return null;
        }

        static string CheckSymmetry(PlacementSpec spec)
        {
            if (spec.Symmetry < 1 || spec.Symmetry > 8)
                return "symmetry must be between 1 and 8.";
            if (spec.Method == SymmetryMethod.Mirror && spec.Symmetry > 2)
                return "Mirror symmetry places at most 2 copies.";
            if (!string.IsNullOrEmpty(spec.ParentNode) && spec.Surface.HasValue)
                return "Specify either parentNode (stack attachment) or surface, not both.";
            if (string.IsNullOrEmpty(spec.ParentNode) && !spec.Surface.HasValue)
                return "Specify parentNode to attach to one of the parent's attach nodes, or surface to attach to its surface.";
            return null;
        }

        /// <summary>
        /// Work out the attachment the editor would make for this placement.
        /// Sets the part's transform to the attached pose.
        /// </summary>
        static string ComputeAttachment(
            EditorLogic editor,
            Part part,
            Part parent,
            PlacementSpec spec,
            bool moving,
            out Attachment attach
        )
        {
            // The pose a held part has before snapping (st_place's update).
            part.transform.rotation = editor.vesselRotation * part.initRotation * part.attRotation;

            string error = spec.Surface.HasValue
                ? SurfaceAttachment(editor, part, parent, spec.Surface.Value, spec.Rotation, out attach)
                : StackAttachment(editor, part, parent, spec, moving, out attach);
            if (error != null)
                return error;

            part.transform.SetPositionAndRotation(attach.position, attach.rotation * part.attRotation);
            part.attRotation0 = attach.rotation;
            part.attPos0 = attach.position;
            part.potentialParent = parent;
            return null;
        }

        /// <summary>Node attachment, as in EditorLogic.checkAttach.</summary>
        static string StackAttachment(
            EditorLogic editor,
            Part part,
            Part parent,
            PlacementSpec spec,
            bool moving,
            out Attachment attach
        )
        {
            attach = null;
            if (!part.attachRules.stack)
                return $"{Title(part)} cannot be attached to nodes. Use surface attachment instead.";
            if (!editor.allowNodeAttachment)
                return "Node attachment is currently disabled in the editor.";
            if (!parent.attachRules.allowStack && !parent.attachRules.allowDock)
                return $"{Describe(parent)} does not accept parts on its attach nodes.";

            var pnode = parent.attachNodes.FirstOrDefault(n =>
                string.Equals(n.id, spec.ParentNode, StringComparison.OrdinalIgnoreCase)
            );
            if (pnode == null)
                return $"{Describe(parent)} has no attach node '{spec.ParentNode}'. Nodes: {NodeList(parent)}.";
            bool pnodeFree =
                pnode.attachedPart == null
                || (moving && pnode.attachedPart == part)
                || CheatOptions.AllowPartClipping;
            if (!pnodeFree)
                return $"Node '{pnode.id}' of {Describe(parent)} is already used by {Describe(pnode.attachedPart)}.";

            // A moved part's node to its old parent is freed by detaching it.
            bool NodeFree(AttachNode n) =>
                n.attachedPart == null || (moving && n.attachedPart == part.parent);

            Vector3 parentDir = parent.transform.TransformDirection(pnode.orientation).normalized;
            AttachNode cnode;
            if (!string.IsNullOrEmpty(spec.Node))
            {
                cnode = part.attachNodes.FirstOrDefault(n =>
                    string.Equals(n.id, spec.Node, StringComparison.OrdinalIgnoreCase)
                );
                if (cnode == null)
                    return $"{Title(part)} has no attach node '{spec.Node}'. Nodes: {NodeList(part)}.";
                if (!NodeFree(cnode))
                    return $"Node '{cnode.id}' of {Title(part)} is already used by {Describe(cnode.attachedPart)}.";
                if (cnode.nodeType != pnode.nodeType)
                    return $"Node '{cnode.id}' ({cnode.nodeType}) cannot connect to node '{pnode.id}' ({pnode.nodeType}).";
            }
            else
            {
                // The free node pointing most directly back at the parent node.
                cnode = part
                    .attachNodes.Where(n => NodeFree(n) && n.nodeType == pnode.nodeType)
                    .OrderBy(n =>
                        Vector3.Dot(
                            part.transform.TransformDirection(n.orientation).normalized,
                            parentDir
                        )
                    )
                    .FirstOrDefault();
                if (cnode == null)
                    return $"{Title(part)} has no free {pnode.nodeType} node to attach with. Nodes: {NodeList(part)}.";
            }

            Vector3 childDir = part.transform.TransformDirection(cnode.orientation).normalized;
            Quaternion stackRotation = Quaternion.FromToRotation(childDir, -parentDir);
            Quaternion rotation =
                Quaternion.AngleAxis(spec.Rotation, parentDir) * stackRotation * editor.vesselRotation;
            Vector3 position =
                parent.transform.TransformPoint(pnode.position + pnode.offset)
                - rotation * part.attRotation * cnode.position;

            part.attachMode = AttachModes.STACK;
            attach = new Attachment
            {
                possible = true,
                mode = AttachModes.STACK,
                caller = part,
                potentialParent = parent,
                callerPartNode = cnode,
                otherPartNode = pnode,
                position = position,
                rotation = rotation,
            };
            return null;
        }

        /// <summary>
        /// Surface attachment, as in EditorLogic.checkAttach, with the mouse
        /// raycast replaced by a ray aimed at the parent.
        /// </summary>
        static string SurfaceAttachment(
            EditorLogic editor,
            Part part,
            Part parent,
            SurfaceParams surface,
            float roll,
            out Attachment attach
        )
        {
            attach = null;
            if (!part.attachRules.srfAttach || part.srfAttachNode == null)
                return $"{Title(part)} cannot be surface attached. Use a stack attach node instead.";
            if (!editor.allowSrfAttachment)
                return "Surface attachment is currently disabled in the editor.";
            if (!parent.attachRules.allowSrfAttach)
                return $"{Describe(parent)} does not allow parts to be attached to its surface.";

            var colliders = parent
                .GetComponentsInChildren<Collider>()
                .Where(c =>
                    // Part colliders are triggers in the editor.
                    c.enabled
                    && !c.CompareTag("NoAttach")
                    && FlightGlobals.GetPartUpwardsCached(c.gameObject) == parent
                )
                .ToList();
            if (colliders.Count == 0)
                return $"{Describe(parent)} has no surface to attach to.";

            var pt = parent.transform;
            Ray ray;
            if (surface.Origin.HasValue || surface.Direction.HasValue)
            {
                if (!surface.Origin.HasValue || !surface.Direction.HasValue)
                    return "surface.origin and surface.direction must be given together.";
                var dir = surface.Direction.Value.ToVector3();
                if (dir.sqrMagnitude < 1e-6f)
                    return "surface.direction must not be zero.";
                ray = new Ray(
                    pt.TransformPoint(surface.Origin.Value.ToVector3()),
                    pt.TransformDirection(dir).normalized
                );
            }
            else
            {
                Bounds bounds = colliders[0].bounds;
                foreach (var c in colliders)
                    bounds.Encapsulate(c.bounds);
                float reach = bounds.extents.magnitude + (bounds.center - pt.position).magnitude + 1f;

                Vector3 outward = Quaternion.AngleAxis(surface.Angle ?? 0f, pt.up) * pt.forward;
                Vector3 center = pt.position + pt.up * (surface.Height ?? 0f);
                ray = new Ray(center + outward * reach, -outward);
            }

            bool found = false;
            RaycastHit hit = default;
            foreach (var c in colliders)
            {
                if (c.Raycast(ray, out var h, 1000f) && (!found || h.distance < hit.distance))
                {
                    hit = h;
                    found = true;
                }
            }
            if (!found)
                return $"The attach ray did not hit {Describe(parent)}. {SurfaceExtentHint(parent, colliders)}";

            Quaternion rotation =
                Quaternion.LookRotation(hit.normal, pt.rotation * Vector3.up)
                * Quaternion.LookRotation(part.srfAttachNode.orientation, Vector3.up);
            rotation = Quaternion.AngleAxis(roll, hit.normal) * rotation;
            Vector3 position = hit.point - rotation * part.attRotation * part.srfAttachNode.position;

            part.attachMode = AttachModes.SRF_ATTACH;
            part.srfAttachNode.srfAttachMeshName = hit.collider.name;
            attach = new Attachment
            {
                possible = true,
                mode = AttachModes.SRF_ATTACH,
                caller = part,
                potentialParent = parent,
                callerPartNode = part.srfAttachNode,
                otherPartNode = null,
                position = position,
                rotation = rotation,
            };
            return null;
        }

        static string SurfaceExtentHint(Part parent, List<Collider> colliders)
        {
            // Height range of the parent's colliders along its up axis.
            var pt = parent.transform;
            float min = float.MaxValue,
                max = float.MinValue;
            foreach (var c in colliders)
            {
                var b = c.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z
                    );
                    float h = Vector3.Dot(corner - pt.position, pt.up);
                    min = Mathf.Min(min, h);
                    max = Mathf.Max(max, h);
                }
            }
            return $"Its surface spans roughly height {min.ToString("0.00", Inv)} to {max.ToString("0.00", Inv)} m along its up axis.";
        }

        static string NodeList(Part p)
        {
            if (p.attachNodes.Count == 0)
                return "(none)";
            return string.Join(
                ", ",
                p.attachNodes.Select(n =>
                    n.attachedPart == null ? $"{n.id} (free)" : $"{n.id} (used by {n.attachedPart.persistentId})"
                )
            );
        }

        /// <summary>
        /// Attach a part that has been posed by ComputeAttachment, creating
        /// symmetry counterparts first. Replicates st_place's per-frame
        /// symmetry update followed by the on_partAttached event and leaving
        /// st_place. Returns all placed parts (the part first).
        /// </summary>
        static List<Part> AttachWithSymmetry(
            EditorLogic editor,
            Part part,
            Attachment attach,
            PlacementSpec spec
        )
        {
            editor.RestoreSymmetryState();
            int oldMode = editor.symmetryMode;
            var oldMethod = editor.symmetryMethod;
            int oldModeBeforeNode = editor.symmetryModeBeforeNodeAttachment;

            editor.selectedPart = part;
            try
            {
                editor.symmetryMethod = spec.Method;
                editor.symmetryMode = spec.Symmetry - 1;
                editor.symmetryModeBeforeNodeAttachment = -1;

                // createSymmetry may change the mode to match the parent's
                // symmetry or a multi-coupler and expects to run again the
                // next frame; repeat until it settles.
                for (int i = 0; i < 4; i++)
                {
                    int mode = editor.symmetryMode;
                    editor.createSymmetry(mode, attach);
                    if (editor.symmetryMode == mode)
                        break;
                }

                if (part.symmetryCounterparts.Count > 0)
                {
                    var symAttachments = editor.CheckSymPartsAttach(attach);
                    editor.RestoreSymmetryState();
                    if (symAttachments.All(a => a != null && a.possible))
                    {
                        editor.attachPart(part, attach);
                        editor.attachSymParts(symAttachments);
                    }
                    else
                    {
                        editor.deleteSymmetryParts();
                        editor.attachPart(part, attach);
                    }
                }
                else
                {
                    editor.attachPart(part, attach);
                }

                editor.SetBackup();
                if (part.CrewCapacity > 0)
                    editor.RefreshCrewAssignment(
                        ShipConstruction.ShipConfig,
                        editor.GetPartExistsFilter()
                    );

                // The inventory UI tracks a newly created cargo part as held,
                // and destroys the held part on PartAttached unless released.
                var inventory = UIPartActionControllerInventory.Instance;
                if (inventory != null)
                {
                    var cargo = part.FindModuleImplementing<ModuleCargoPart>();
                    if (
                        (cargo != null && !cargo.IsDeployedSciencePart())
                        || inventory.CurrentCargoPart == part
                    )
                    {
                        inventory.CurrentCargoPart = null;
                        inventory.CurrentInventory = null;
                    }
                    inventory.DestroyHeldPartAsIcon();
                }
                GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartAttached, part);

                // st_place.OnLeave
                part.gameObject.SetLayerRecursive(0, filterTranslucent: true, PartLayerIgnoreMask);
                editor.cleanSymmetry();
                editor.CheckCopiedSymmetryPersistentId(part);
                GameEvents.onEditorPartPlaced.Fire(part);
            }
            finally
            {
                editor.selectedPart = null;
                editor.RestoreSymmetryState();
                editor.symmetryMode = oldMode;
                editor.symmetryMethod = oldMethod;
                editor.symmetryModeBeforeNodeAttachment = oldModeBeforeNode;
                editor.SetSymState();
            }

            var placed = new List<Part> { part };
            placed.AddRange(part.symmetryCounterparts.Where(c => c != null));
            return placed;
        }

        static JsonObject PlacementResult(
            EditorLogic editor,
            Part part,
            PlacementSpec spec,
            List<Part> placed
        )
        {
            var stages = StageNumbers();
            var result = new JsonObject
            {
                ["part"] = PartJson(part, editor, stages, true, false),
                ["symmetry"] = placed.Count,
            };
            if (placed.Count > 1)
                result["symmetryCounterparts"] = new JsonArray(
                    placed.Skip(1).Select(c => (JsonNode)PartJson(c, editor, stages, false, false)).ToArray()
                );
            if (placed.Count != spec.Symmetry)
                result["note"] =
                    $"Requested symmetry {spec.Symmetry} but the editor placed {placed.Count} (symmetry follows a symmetric parent or multi-coupler, and parts on the parent's axis get no radial copies).";
            return result;
        }

        #endregion

        #region Remove / Transform

        /// <summary>
        /// Check the editor is idle and find a part on the ship.
        /// </summary>
        static string FindPartToEdit(long id, bool requireIdle, out EditorLogic editor, out Part part)
        {
            part = null;
            return CheckEditor(out editor, requireIdle) ?? FindShipPart(editor, id, out part);
        }

        McpProtocol.ToolCallResult RemovePart(PartIdParams args)
        {
            string error = FindPartToEdit(args.PartId, true, out var editor, out var part);
            if (error != null)
                return McpProtocol.ToolError(error);

            if (part == editor.rootPart)
                return McpProtocol.ToolError(
                    "The root part cannot be removed (that would delete the whole ship). Use ksp_editor_new_ship to start over."
                );

            var removed = new List<Part>();
            foreach (var p in new[] { part }.Concat(part.symmetryCounterparts))
            {
                if (p != null)
                    removed.AddRange(EditorLogicBase.FindPartsInChildren(p));
            }
            var removedIds = new JsonArray(
                removed.Distinct().Select(p => (JsonNode)p.persistentId).ToArray()
            );

            // Replicates picking the part up and deleting it (on_partPicked
            // followed by DestroySelectedPart).
            editor.selectedPart = part;
            try
            {
                editor.detachPart(part);
                editor.deleteSymmetryParts();
                GameEvents.onEditorPartPicked.Fire(part);
                editor.SetBackup();
                GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartDeleted, part);
                editor.deletePart(part, symmetry: false, printConfirmation: true);
                editor.RefreshCrewAssignment(ShipConstruction.ShipConfig, editor.GetPartExistsFilter());
            }
            finally
            {
                editor.selectedPart = null;
            }
            GameEvents.onEditorPartDeleted.Fire(part);

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["removedPartIds"] = removedIds,
                    ["partCount"] = editor.ship.parts.Count,
                }
            );
        }

        McpProtocol.ToolCallResult TransformPart(TransformPartParams args)
        {
            string error = FindPartToEdit(args.PartId, true, out var editor, out var part);
            if (error != null)
                return McpProtocol.ToolError(error);

            if (!args.Offset.HasValue && !args.Rotate.HasValue)
                return McpProtocol.ToolError("Specify offset and/or rotate.");

            bool local;
            switch ((args.Space ?? "world").ToLowerInvariant())
            {
                case "world":
                    local = false;
                    break;
                case "local":
                    local = true;
                    break;
                default:
                    return McpProtocol.ToolError($"Unknown space '{args.Space}'. Use world or local.");
            }

            var t = part.GetReferenceTransform();
            var attachNode = part.parent != null ? part.FindAttachNodeByPart(part.parent) : null;
            int symCount = part.symmetryCounterparts.Count;
            bool clamped = false;

            if (args.Rotate.HasValue)
            {
                // onRotateGizmoUpdate: rotate about the node attaching the part.
                var euler = Quaternion.Euler(args.Rotate.Value.ToVector3());
                Vector3 pivot = attachNode != null ? t.TransformPoint(attachNode.position) : t.position;
                Quaternion attRotation = part.attRotation;
                Quaternion attRotation0 = part.attRotation0;
                t.rotation = local ? t.rotation * euler : euler * t.rotation;
                part.attRotation0 = part.transform.localRotation;
                part.attRotation = attRotation * Quaternion.Inverse(attRotation0) * part.transform.localRotation;
                if (attachNode != null)
                    t.position += pivot - t.TransformPoint(attachNode.position);
                if (symCount > 0)
                    editor.UpdateSymmetry(part, symCount, part.parent, attachNode);
                GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartRotated, part);
            }

            if (args.Offset.HasValue)
            {
                // onOffsetGizmoUpdate, including its limit on pulling a part
                // away from its parent.
                var offset = args.Offset.Value.ToVector3();
                t.position += local ? t.TransformDirection(offset) : offset;
                if (part != editor.rootPart && part.parent != null)
                    clamped = ClampOffset(part, t);
                part.attPos = t.localPosition - part.attPos0;
                if (symCount > 0)
                {
                    editor.UpdateSymmetry(part, symCount, part.parent, attachNode);
                    foreach (var c in part.symmetryCounterparts)
                        c.attPos = c.transform.localPosition - c.attPos0;
                }
                GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartOffset, part);
            }

            editor.SetBackup();

            var result = new JsonObject
            {
                ["part"] = PartJson(part, editor, StageNumbers(), false, false),
            };
            if (clamped)
                result["note"] =
                    "The offset was limited to keep the part touching its parent, as the offset gizmo does.";
            return McpProtocol.ToolSuccess(result);
        }

        static bool ClampOffset(Part part, Transform t)
        {
            const float threshold = 0.1f;
            bool clamped = false;
            if (part.surfaceAttachGO != null)
                part.surfaceAttachGO.SetActive(true);
            if (
                EditorGeometryUtil.TestPartBoundsSeparate(
                    part,
                    part.GetReferenceParent(),
                    threshold,
                    out Vector3 gap
                )
            )
            {
                if (part.attachMode == AttachModes.STACK)
                {
                    var childToParent = part.FindAttachNodeByPart(part.parent);
                    var parentToChild = part.parent.FindAttachNodeByPart(part);
                    if (childToParent != null && parentToChild != null)
                    {
                        Vector3 diff =
                            parentToChild.position
                            - (t.localPosition + part.attRotation * childToParent.position);
                        if (diff.sqrMagnitude > threshold * (parentToChild.size + 1))
                        {
                            t.position -= gap;
                            clamped = true;
                        }
                    }
                }
                else
                {
                    t.position -= gap;
                    clamped = true;
                }
            }
            if (part.surfaceAttachGO != null)
                part.surfaceAttachGO.SetActive(false);
            return clamped;
        }

        #endregion

        #region Part Properties

        McpProtocol.ToolCallResult GetPart(PartIdParams args)
        {
            string error = FindPartToEdit(args.PartId, false, out var editor, out var part);
            if (error != null)
                return McpProtocol.ToolError(error);
            return McpProtocol.ToolSuccess(PartDetailJson(editor, part));
        }

        static JsonObject PartDetailJson(EditorLogic editor, Part part)
        {
            var json = PartJson(part, editor, StageNumbers(), false, false);
            var origin = editor.rootPart != null ? editor.rootPart.transform.position : Vector3.zero;

            var nodes = new JsonArray();
            foreach (var n in part.attachNodes)
                nodes.Add(NodeJson(n, origin, part));
            json["nodes"] = nodes;
            json["canSurfaceAttach"] = part.attachRules.srfAttach;
            json["allowsSurfaceAttach"] = part.attachRules.allowSrfAttach;
            json["rotation"] = Vec(part.transform.rotation.eulerAngles, 2);

            var children = new JsonArray();
            foreach (var c in part.children)
                children.Add((JsonNode)c.persistentId);
            json["childIds"] = children;

            if (part.Resources.Count > 0)
                json["resources"] = ResourcesJson(part);

            var modules = new JsonArray();
            AddModuleJson(modules, -1, "Part", part.Fields, part.Events, part.Actions);
            for (int i = 0; i < part.Modules.Count; i++)
            {
                var m = part.Modules[i];
                AddModuleJson(modules, i, m.moduleName, m.Fields, m.Events, m.Actions);
            }
            json["modules"] = modules;
            return json;
        }

        static void AddModuleJson(
            JsonArray modules,
            int index,
            string name,
            BaseFieldList fieldList,
            BaseEventList eventList,
            BaseActionList actionList
        )
        {
            var fields = new JsonArray();
            foreach (BaseField f in fieldList)
            {
                if (!FieldVisible(f))
                    continue;
                var entry = new JsonObject
                {
                    ["name"] = f.name,
                    ["label"] = Localizer.Format(f.guiName),
                    ["value"] = FieldValueJson(f),
                    ["editable"] = FieldEditable(f),
                };
                if (!string.IsNullOrEmpty(f.guiUnits))
                    entry["units"] = f.guiUnits;
                AddControlJson(entry, f);
                fields.Add(entry);
            }

            var events = new JsonArray();
            foreach (BaseEvent e in eventList)
            {
                if (!EventVisible(e))
                    continue;
                events.Add(new JsonObject { ["name"] = e.name, ["label"] = e.GUIName });
            }

            var actions = new JsonArray();
            if (actionList != null)
            {
                foreach (BaseAction a in actionList)
                {
                    if (!a.activeEditor || a.noLongerAssignable)
                        continue;
                    actions.Add(
                        new JsonObject
                        {
                            ["name"] = a.name,
                            ["label"] = a.guiName,
                            ["groups"] = new JsonArray(
                                GroupNames(a.actionGroup).Select(g => (JsonNode)g).ToArray()
                            ),
                        }
                    );
                }
            }

            if (fields.Count == 0 && events.Count == 0 && actions.Count == 0)
                return;

            var module = new JsonObject { ["index"] = index, ["module"] = name };
            if (fields.Count > 0)
                module["fields"] = fields;
            if (events.Count > 0)
                module["events"] = events;
            if (actions.Count > 0)
                module["actions"] = actions;
            modules.Add(module);
        }

        // The part action window's visibility rules in the editor.
        static bool FieldVisible(BaseField f) =>
            f.guiActiveEditor
            && (!f.advancedTweakable || GameSettings.ADVANCED_TWEAKABLES)
            && (f.uiControlEditor == null || (f.uiControlEditor.scene & UI_Scene.Editor) != 0);

        static bool FieldEditable(BaseField f) =>
            f.uiControlEditor != null
            && !(f.uiControlEditor is UI_Label)
            && f.uiControlEditor.controlEnabled
            && f.guiInteractable;

        static bool EventVisible(BaseEvent e) =>
            e.guiActiveEditor && e.active && (!e.advancedTweakable || GameSettings.ADVANCED_TWEAKABLES);

        static JsonNode FieldValueJson(BaseField f)
        {
            object value = f.GetValue(f.host);
            if (f.uiControlEditor is UI_VariantSelector vs && value is int index)
                return vs.variants != null && index >= 0 && index < vs.variants.Count
                    ? vs.variants[index].Name
                    : index.ToString(Inv);
            return value switch
            {
                null => null,
                bool b => b,
                int i => i,
                float fl => R(fl, 4),
                double d => R(d, 4),
                Enum en => en.ToString(),
                _ => value.ToString(),
            };
        }

        static void AddControlJson(JsonObject entry, BaseField f)
        {
            switch (f.uiControlEditor)
            {
                case UI_FloatRange range:
                    entry["control"] = "range";
                    entry["min"] = R(range.minValue, 4);
                    entry["max"] = R(range.maxValue, 4);
                    entry["step"] = R(range.stepIncrement, 4);
                    break;
                case UI_FloatEdit edit:
                    entry["control"] = "edit";
                    if (!float.IsInfinity(edit.minValue))
                        entry["min"] = R(edit.minValue, 4);
                    if (!float.IsInfinity(edit.maxValue))
                        entry["max"] = R(edit.maxValue, 4);
                    break;
                case UI_Toggle toggle:
                    entry["control"] = "toggle";
                    entry["enabledText"] = toggle.displayEnabledText;
                    entry["disabledText"] = toggle.displayDisabledText;
                    break;
                case UI_VariantSelector vs:
                    entry["control"] = "variant";
                    if (vs.variants != null)
                        entry["options"] = new JsonArray(
                            vs.variants.Select(v => (JsonNode)v.Name).ToArray()
                        );
                    break;
                case UI_ChooseOption choose:
                    entry["control"] = "choice";
                    if (choose.options != null)
                        entry["options"] = new JsonArray(
                            choose.options.Select(o => (JsonNode)o).ToArray()
                        );
                    if (choose.display != null)
                        entry["optionLabels"] = new JsonArray(
                            choose.display.Select(o => (JsonNode)Localizer.Format(o)).ToArray()
                        );
                    break;
                case UI_Label:
                case null:
                    break;
                default:
                    entry["control"] = f.uiControlEditor.GetType().Name;
                    break;
            }
        }

        McpProtocol.ToolCallResult ModifyPart(ModifyPartParams args)
        {
            string error = FindPartToEdit(args.PartId, true, out var editor, out var part);
            if (error != null)
                return McpProtocol.ToolError(error);
            bool sym = args.ApplySymmetry ?? true;

            var applied = new JsonArray();
            var errors = new JsonArray();

            foreach (var change in args.Fields ?? Array.Empty<FieldChange>())
            {
                string label = $"field {change.Module}.{change.Field}";
                string err = SetField(editor, part, change, sym);
                (err == null ? applied : errors).Add(err == null ? label : $"{label}: {err}");
            }

            foreach (var change in args.Resources ?? Array.Empty<ResourceChange>())
            {
                string label = $"resource {change.Resource}";
                string err = SetResource(editor, part, change, sym);
                (err == null ? applied : errors).Add(err == null ? label : $"{label}: {err}");
            }

            foreach (var call in args.Events ?? Array.Empty<EventCall>())
            {
                string label = $"event {call.Module}.{call.Event}";
                string err = InvokeEvent(part, call);
                (err == null ? applied : errors).Add(err == null ? label : $"{label}: {err}");
            }

            bool actionsChanged = false;
            foreach (var change in args.Actions ?? Array.Empty<ActionChange>())
            {
                string label = $"action {change.Module}.{change.Action}";
                string err = SetActionGroups(part, change, sym);
                (err == null ? applied : errors).Add(err == null ? label : $"{label}: {err}");
                actionsChanged |= err == null;
            }

            // The action group editor records undo states; the part action
            // window does not.
            if (actionsChanged)
                editor.SetBackup();

            if (applied.Count > 0)
                GameEvents.onEditorShipModified.Fire(editor.ship);

            if (applied.Count == 0 && errors.Count == 0)
                return McpProtocol.ToolError("Nothing to change: specify fields, resources, events or actions.");
            if (applied.Count == 0)
                return McpProtocol.ToolError(
                    "No changes were applied:\n" + string.Join("\n", errors.Select(e => e.ToString()))
                );

            var result = new JsonObject { ["applied"] = applied };
            if (errors.Count > 0)
                result["errors"] = errors;
            result["part"] = PartDetailJson(editor, part);
            return McpProtocol.ToolSuccess(result);
        }

        /// <summary>
        /// Find a module (or the part itself, for "Part") the way callers name
        /// it. Returns the part or module whose Fields/Events/Actions to use.
        /// </summary>
        static string FindModule(Part part, string name, int? index, out object host)
        {
            host = null;
            if (index.HasValue)
            {
                if (index.Value == -1)
                {
                    host = part;
                    return null;
                }
                if (index.Value < 0 || index.Value >= part.Modules.Count)
                    return $"moduleIndex {index.Value} is out of range (the part has {part.Modules.Count} modules).";
                var m = part.Modules[index.Value];
                if (!string.IsNullOrEmpty(name) && !string.Equals(m.moduleName, name, StringComparison.OrdinalIgnoreCase))
                    return $"Module {index.Value} is {m.moduleName}, not {name}.";
                host = m;
                return null;
            }

            if (string.IsNullOrEmpty(name))
                return "Specify module (and moduleIndex if several modules share the name).";
            if (string.Equals(name, "Part", StringComparison.OrdinalIgnoreCase))
            {
                host = part;
                return null;
            }

            var matches = new List<PartModule>();
            foreach (PartModule m in part.Modules)
            {
                if (string.Equals(m.moduleName, name, StringComparison.OrdinalIgnoreCase))
                    matches.Add(m);
            }
            if (matches.Count == 0)
                return $"The part has no module '{name}'. Use ksp_editor_get_part to see its modules.";
            if (matches.Count > 1)
                return $"The part has {matches.Count} '{name}' modules; specify moduleIndex.";
            host = matches[0];
            return null;
        }

        static BaseFieldList FieldsOf(object host) =>
            host is Part p ? p.Fields : ((PartModule)host).Fields;

        static string SetField(EditorLogic editor, Part part, FieldChange change, bool applySymmetry)
        {
            string error = FindModule(part, change.Module, change.ModuleIndex, out var host);
            if (error != null)
                return error;

            BaseField field = null;
            foreach (BaseField f in FieldsOf(host))
            {
                if (f.name == change.Field)
                {
                    field = f;
                    break;
                }
                if (field == null && string.Equals(Localizer.Format(f.guiName), change.Field, StringComparison.OrdinalIgnoreCase))
                    field = f;
            }
            if (field == null || !FieldVisible(field))
                return "No such field in the part action window.";
            if (!FieldEditable(field))
                return "The field is read-only.";
            if (change.Value == null)
                return "value is missing.";

            if (field.uiControlEditor is UI_VariantSelector)
                return SetVariant(editor, part, change.Value, applySymmetry);

            error = ParseFieldValue(field, change.Value, out object value);
            if (error != null)
                return error;

            ApplyFieldValue(part, host, field, value, applySymmetry);
            return null;
        }

        static string ParseFieldValue(BaseField field, string text, out object value)
        {
            value = null;
            Type type = field.FieldInfo.FieldType;
            var control = field.uiControlEditor;

            if (control is UI_ChooseOption choose && choose.options != null)
            {
                int idx = Array.FindIndex(
                    choose.options,
                    o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase)
                );
                if (idx < 0 && choose.display != null)
                    idx = Array.FindIndex(
                        choose.display,
                        d => string.Equals(Localizer.Format(d), text, StringComparison.OrdinalIgnoreCase)
                    );
                if (type == typeof(string))
                {
                    if (idx < 0)
                        return $"'{text}' is not one of the options: {string.Join(", ", choose.options)}.";
                    value = choose.options[idx];
                    return null;
                }
                if (type == typeof(int))
                {
                    if (idx < 0 && !(int.TryParse(text, NumberStyles.Integer, Inv, out idx) && idx >= 0 && idx < choose.options.Length))
                        return $"'{text}' is not one of the options: {string.Join(", ", choose.options)}.";
                    value = idx;
                    return null;
                }
            }

            if (type == typeof(string))
                value = text;
            else if (type == typeof(bool))
            {
                if (!bool.TryParse(text, out bool b))
                    return $"'{text}' is not true or false.";
                value = b;
            }
            else if (type == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, Inv, out int i))
                    return $"'{text}' is not an integer.";
                value = i;
            }
            else if (type == typeof(float) || type == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, Inv, out double d))
                    return $"'{text}' is not a number.";
                float min = float.NegativeInfinity,
                    max = float.PositiveInfinity;
                if (control is UI_FloatRange range)
                {
                    min = range.minValue;
                    max = range.maxValue;
                }
                else if (control is UI_FloatEdit edit)
                {
                    min = edit.minValue;
                    max = edit.maxValue;
                }
                if (d < min - 1e-6 || d > max + 1e-6)
                    return $"{text} is outside the allowed range {min.ToString(Inv)} to {max.ToString(Inv)}.";
                value = type == typeof(float) ? (object)(float)d : d;
            }
            else if (type.IsEnum)
            {
                try
                {
                    value = Enum.Parse(type, text, true);
                }
                catch (ArgumentException)
                {
                    return $"'{text}' is not one of: {string.Join(", ", Enum.GetNames(type))}.";
                }
            }
            else
            {
                return $"Fields of type {type.Name} are not supported.";
            }
            return null;
        }

        /// <summary>Replicates UIPartActionFieldItem.SetFieldValue.</summary>
        static void ApplyFieldValue(Part part, object host, BaseField field, object value, bool applySymmetry)
        {
            var control = field.uiControlEditor;
            object oldValue = field.GetValue(field.host);
            bool changed = !Equals(oldValue, value);

            if (applySymmetry && (control.affectSymCounterparts & UI_Scene.Editor) != UI_Scene.None)
            {
                foreach (var counterpart in part.symmetryCounterparts)
                {
                    BaseFieldList fields;
                    if (host is PartModule module)
                    {
                        int index = part.Modules.IndexOf(module);
                        PartModule other =
                            index < counterpart.Modules.Count
                            && counterpart.Modules[index] != null
                            && counterpart.Modules[index].GetType() == module.GetType()
                                ? counterpart.Modules[index]
                                : counterpart.Modules[module.ClassName];
                        if (other == null)
                            continue;
                        fields = other.Fields;
                    }
                    else
                    {
                        fields = counterpart.Fields;
                    }

                    changed |= !Equals(fields.GetValue(field.name), value);
                    fields.SetValue(field.name, value);
                    var otherField = fields[field.name];
                    otherField?.uiControlEditor?.onSymmetryFieldChanged?.Invoke(field, value);
                    otherField?.uiControlFlight?.onSymmetryFieldChanged?.Invoke(field, value);
                }
            }

            if (!changed)
                return;
            field.SetValue(value, field.host);
            control.onFieldChanged?.Invoke(field, oldValue);
        }

        /// <summary>
        /// Select a variant like the part action window's variant selector,
        /// which locks variants of another size group while parts are surface
        /// attached to the part.
        /// </summary>
        static string SetVariant(EditorLogic editor, Part part, string name, bool applySymmetry)
        {
            var variants = part.variants;
            if (variants == null)
                return "The part has no variants.";
            int index = variants.variantList.FindIndex(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)
            );
            if (index < 0)
                return $"No variant '{name}'. Variants: {string.Join(", ", variants.GetVariantNames())}.";

            var field = variants.Fields["variantIndex"];
            if (field == null)
                return "The part's variant selector could not be found.";

            int current = variants.GetCurrentVariantIndex();
            bool hasSurfaceChildren = part.children.Any(c => c.attachMode == AttachModes.SRF_ATTACH);
            if (
                hasSurfaceChildren
                && current >= 0
                && variants.variantList[index].SizeGroup != variants.variantList[current].SizeGroup
            )
                return $"Variant '{variants.variantList[index].Name}' is locked because parts are surface attached to this part and it has a different size.";

            ApplyFieldValue(part, variants, field, index, applySymmetry);
            GameEvents.onEditorShipModified.Fire(editor.ship);
            return null;
        }

        static string SetResource(EditorLogic editor, Part part, ResourceChange change, bool applySymmetry)
        {
            var resource = part.Resources[change.Resource];
            if (resource == null)
                return $"The part has no resource '{change.Resource}'. Resources: {string.Join(", ", part.Resources.Select(r => r.resourceName))}.";

            double? amount = change.Amount;
            if (change.Fraction.HasValue)
            {
                if (amount.HasValue)
                    return "Specify amount or fraction, not both.";
                amount = change.Fraction.Value * resource.maxAmount;
            }
            if (amount.HasValue && !resource.isTweakable)
                return "This resource's amount cannot be changed in the editor.";
            if (!amount.HasValue && !change.Flow.HasValue)
                return "Specify amount, fraction or flow.";
            if (amount.HasValue)
                amount = Math.Max(0, Math.Min(resource.maxAmount, amount.Value));

            // UIPartActionResourceEditor applies edits to symmetry counterparts.
            IEnumerable<Part> targets = applySymmetry
                ? new[] { part }.Concat(part.symmetryCounterparts)
                : new[] { part };
            foreach (var p in targets)
            {
                var r = p.Resources[change.Resource];
                if (r == null)
                    continue;
                if (amount.HasValue)
                    r.amount = Math.Min(r.maxAmount, amount.Value);
                if (change.Flow.HasValue)
                    r.flowState = change.Flow.Value;
            }
            return null;
        }

        static string InvokeEvent(Part part, EventCall call)
        {
            string error = FindModule(part, call.Module, call.ModuleIndex, out var host);
            if (error != null)
                return error;
            var events = host is Part p ? p.Events : ((PartModule)host).Events;

            BaseEvent evt = null;
            foreach (BaseEvent e in events)
            {
                if (!EventVisible(e))
                    continue;
                if (e.name == call.Event)
                {
                    evt = e;
                    break;
                }
                if (evt == null && string.Equals(e.GUIName, call.Event, StringComparison.OrdinalIgnoreCase))
                    evt = e;
            }
            if (evt == null)
                return "No such button in the part action window (it may be hidden in the current state).";
            evt.Invoke();
            return null;
        }

        static string SetActionGroups(Part part, ActionChange change, bool applySymmetry)
        {
            string error = FindModule(part, change.Module, change.ModuleIndex, out var host);
            if (error != null)
                return error;
            var actions = host is Part p ? p.Actions : ((PartModule)host).Actions;
            var action = actions[change.Action];
            if (action == null || !action.activeEditor || action.noLongerAssignable)
                return "No such action that can be assigned to action groups.";

            var groups = KSPActionGroup.None;
            foreach (string name in change.Groups ?? Array.Empty<string>())
            {
                KSPActionGroup group;
                try
                {
                    group = (KSPActionGroup)Enum.Parse(typeof(KSPActionGroup), name, true);
                }
                catch (ArgumentException)
                {
                    return $"Unknown action group '{name}'.";
                }
                if (group <= KSPActionGroup.None || !AssignableGroups.Contains(group))
                    return $"Unknown action group '{name}'.";
                groups |= group;
            }

            // Same gates as the editor's action group panel.
            float level = ScenarioUpgradeableFacilities.GetFacilityLevel(EditorDriver.editorFacility.ToFacility());
            bool isVab = EditorDriver.editorFacility == EditorFacility.VAB;
            if (!GameVariables.Instance.UnlockedActionGroupsStock(level, isVab))
                return "Action groups are not available until the editor facility is upgraded.";
            const KSPActionGroup customGroups = (KSPActionGroup)0x3FF80; // Custom01-Custom10
            if ((groups & customGroups) != 0 && !GameVariables.Instance.UnlockedActionGroupsCustom(level, isVab))
                return "Custom action groups are not available until the editor facility is upgraded.";

            IEnumerable<Part> targets = applySymmetry
                ? new[] { part }.Concat(part.symmetryCounterparts)
                : new[] { part };
            foreach (var target in targets)
            {
                BaseActionList list;
                if (host is PartModule module)
                {
                    int index = part.Modules.IndexOf(module);
                    if (index >= target.Modules.Count)
                        continue;
                    list = target.Modules[index].Actions;
                }
                else
                {
                    list = target.Actions;
                }
                var a = list[change.Action];
                if (a != null)
                    a.actionGroup = groups;
            }
            return null;
        }

        static readonly KSPActionGroup[] AssignableGroups =
        {
            KSPActionGroup.Stage,
            KSPActionGroup.Gear,
            KSPActionGroup.Light,
            KSPActionGroup.RCS,
            KSPActionGroup.SAS,
            KSPActionGroup.Brakes,
            KSPActionGroup.Abort,
            KSPActionGroup.Custom01,
            KSPActionGroup.Custom02,
            KSPActionGroup.Custom03,
            KSPActionGroup.Custom04,
            KSPActionGroup.Custom05,
            KSPActionGroup.Custom06,
            KSPActionGroup.Custom07,
            KSPActionGroup.Custom08,
            KSPActionGroup.Custom09,
            KSPActionGroup.Custom10,
        };

        static IEnumerable<string> GroupNames(KSPActionGroup groups) =>
            AssignableGroups.Where(g => (groups & g) != 0).Select(g => g.ToString());

        #endregion

        #region Staging

        IEnumerator SetStage(SetStageParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            string error = FindPartToEdit(args.PartId, true, out _, out var part);
            var sm = StageManager.Instance;
            if (error == null && sm == null)
                error = "The staging list is not available.";
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            var icon = part.stackIcon?.StageIcon;
            if (icon == null)
            {
                callback(
                    McpProtocol.ToolError(
                        $"{Describe(part)} has no staging icon (it is not stageable, or staging is disabled for it)."
                    )
                );
                yield break;
            }
            // Symmetry groups are listed under their lead icon.
            var top = icon.groupLead != null ? icon.groupLead : icon;
            var oldGroup = top.Stage;

            bool newStage = args.NewStage ?? false;
            int maxStage = newStage ? sm.Stages.Count : sm.Stages.Count - 1;
            if (args.Stage < 0 || args.Stage > maxStage)
            {
                callback(
                    McpProtocol.ToolError(
                        $"Stage {args.Stage} is out of range (0 to {maxStage}{(newStage ? " for a new stage" : "")})."
                    )
                );
                yield break;
            }
            if (oldGroup == null)
            {
                callback(McpProtocol.ToolError("The part's staging icon is not in a stage."));
                yield break;
            }

            if (newStage)
            {
                // StageGroup.AddStageAfter.
                sm.IncrementCurrentStage();
                sm.AddStageAt(args.Stage);
                sm.SetManualStageOffset(args.Stage);
                StageManager.SetSeparationIndices();
                GameEvents.StageManager.OnGUIStageAdded.Fire(args.Stage);
            }

            var group = sm.Stages[args.Stage];
            if (group != oldGroup)
            {
                // StageIcon.OnEndDrag dropping onto another stage.
                oldGroup.RemoveIcon(top, changeParents: false);
                group.AddIconAt(top, group.Icons.Count, -1);
                top.SetInStageIndexOFGroupedIcons();
                top.SetSymmetryMarkers();
                top.SetManualStageOffset(group.inverseStageIndex);
                oldGroup.UpdateInStageIndexes();
                group.UpdateInStageIndexes();
                if (oldGroup.Icons.Count == 0)
                    sm.SortIcons(instant: false);
                else
                    StageManager.SetSeparationIndices();
                GameEvents.StageManager.OnGUIStageSequenceModified.Fire();
            }

            // Let the staging list re-sort.
            for (int i = 0; i < 5; i++)
                yield return null;

            var stages = StageNumbers();
            callback(
                McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["stage"] = stages.TryGetValue(part, out int s) ? s : null,
                        ["staging"] = StagingJson(),
                    }
                )
            );
        }

        #endregion

        #region Ship Files

        IEnumerator Undo(UndoParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            string error = CheckEditor(out var editor);
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            bool redo = args.Redo ?? false;
            int steps = Math.Max(1, args.Steps ?? 1);
            int done = 0;
            for (int i = 0; i < steps; i++)
            {
                int before = editor.undoLevel;
                editor.RestoreState(redo ? 1 : -1);
                if (editor.undoLevel == before)
                    break;
                done++;
            }

            for (int i = 0; i < 3; i++)
                yield return null;

            if (done == 0)
            {
                callback(McpProtocol.ToolError(redo ? "Nothing to redo." : "Nothing to undo."));
                yield break;
            }

            var result = ShipSummary(editor);
            result[redo ? "redoneSteps" : "undoneSteps"] = done;
            result["note"] = "The ship was reloaded and part ids have changed.";
            callback(McpProtocol.ToolSuccess(result));
        }

        IEnumerator NewShip(NewShipParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            string error = CheckEditor(out var editor);
            if (error == null && !HighLogic.CurrentGame.Parameters.Editor.CanStartNew)
                error = "Starting a new ship is disabled for this game.";
            if (error == null && HasUnsavedChanges(editor) && !(args.DiscardChanges ?? false))
                error = "The current ship has unsaved changes. Save it with ksp_editor_save_craft, or pass discardChanges=true.";
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            // NewShip's "don't save" choice.
            editor.OnNewShipConfirm();
            for (int i = 0; i < 3; i++)
                yield return null;
            callback(McpProtocol.ToolSuccess(ShipSummary(EditorLogic.fetch)));
        }

        McpProtocol.ToolCallResult ListCraft(ListCraftParams args)
        {
            string error = CheckEditor(out _, requireIdle: false);
            if (error != null)
                return McpProtocol.ToolError(error);

            var facilities = new List<EditorFacility>();
            if (string.IsNullOrEmpty(args.Facility) || args.Facility.Equals("VAB", StringComparison.OrdinalIgnoreCase))
                facilities.Add(EditorFacility.VAB);
            if (string.IsNullOrEmpty(args.Facility) || args.Facility.Equals("SPH", StringComparison.OrdinalIgnoreCase))
                facilities.Add(EditorFacility.SPH);
            if (facilities.Count == 0)
                return McpProtocol.ToolError($"Unknown facility '{args.Facility}'. Use VAB or SPH.");

            bool includeStock =
                (args.IncludeStock ?? true) && HighLogic.CurrentGame.Parameters.Difficulty.AllowStockVessels;

            var craft = new JsonArray();
            foreach (var facility in facilities)
            {
                string sub = ShipConstruction.GetShipsSubfolderFor(facility);
                AddCraftFiles(craft, ShipConstruction.GetCurrentGameShipsPathFor(facility), facility, stock: false);
                if (includeStock)
                    AddCraftFiles(craft, KSPUtil.ApplicationRootPath + "Ships/" + sub + "/", facility, stock: true);
            }

            return McpProtocol.ToolSuccess(new JsonObject { ["craft"] = craft });
        }

        static void AddCraftFiles(JsonArray craft, string dir, EditorFacility facility, bool stock)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.craft").OrderByDescending(f => f.LastWriteTime))
            {
                craft.Add(
                    new JsonObject
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(file.Name),
                        ["path"] = RelativeToRoot(file.FullName),
                        ["facility"] = facility.ToString(),
                        ["stock"] = stock,
                        ["lastWriteTime"] = file.LastWriteTime.ToString("o"),
                    }
                );
            }
        }

        static string KspRoot => Path.GetFullPath(KSPUtil.ApplicationRootPath);

        static string RelativeToRoot(string fullPath)
        {
            string root = KspRoot;
            fullPath = Path.GetFullPath(fullPath);
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                fullPath = fullPath.Substring(root.Length);
            return fullPath.Replace('\\', '/').TrimStart('/');
        }

        IEnumerator LoadCraft(LoadCraftParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            string error = CheckEditor(out var editor);
            string fullPath = null;
            if (error == null)
            {
                if (!HighLogic.CurrentGame.Parameters.Editor.CanLoad)
                    error = "Loading craft is disabled for this game.";
                else
                {
                    fullPath = Path.GetFullPath(Path.Combine(KspRoot, args.Path));
                    if (!fullPath.StartsWith(KspRoot, StringComparison.OrdinalIgnoreCase))
                        error = "The craft path must be inside the KSP folder.";
                    else if (!fullPath.EndsWith(".craft", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                        error = $"Craft file '{args.Path}' not found. Use ksp_editor_list_craft.";
                    else if (HasUnsavedChanges(editor) && !(args.DiscardChanges ?? false))
                        error = "The current ship has unsaved changes. Save it with ksp_editor_save_craft, or pass discardChanges=true.";
                }
            }
            if (error != null)
            {
                callback(McpProtocol.ToolError(error));
                yield break;
            }

            // The craft browser's Load choice.
            editor.ShipToLoadSelected(fullPath, CraftBrowserDialog.LoadType.Normal);
            for (int i = 0; i < 5; i++)
                yield return null;

            editor = EditorLogic.fetch;
            if (editor == null || editor.ship == null || editor.ship.parts.Count == 0)
            {
                callback(
                    McpProtocol.ToolError(
                        "The craft did not load (it may use parts that are missing or unavailable). Take a screenshot to check for a dialog."
                    )
                );
                yield break;
            }
            callback(McpProtocol.ToolSuccess(ShipSummary(editor)));
        }

        McpProtocol.ToolCallResult SaveCraft(SaveCraftParams args)
        {
            string error = CheckEditor(out var editor);
            if (error == null && !HighLogic.CurrentGame.Parameters.Editor.CanSave)
                error = "Saving craft is disabled for this game.";
            if (error == null && !editor.AllowSave)
                error = "Saving is currently disabled in the editor.";
            if (error == null && editor.ship.parts.Count == 0)
                error = "There is no ship to save.";
            if (error != null)
                return McpProtocol.ToolError(error);

            ApplyShipInfo(editor, args.Name, args.Description);

            string name = KSPUtil.SanitizeString(editor.shipNameField.text, '_', replaceEmpty: false);
            if (string.IsNullOrEmpty(name))
                name = Localizer.Format("#autoLOC_900530");
            string path = ShipConstruction.GetSavePath(name);

            // saveShip asks before overwriting a different craft's file.
            bool overwriting = name != editor.vesselNameAtLastSave_Sanitized && File.Exists(path);
            if (overwriting && !(args.Overwrite ?? false))
                return McpProtocol.ToolError(
                    $"A craft named '{name}' already exists. Pass overwrite=true to replace it, or choose another name."
                );

            var mode = HighLogic.CurrentGame.Mode;
            if (mode == Game.Modes.MISSION || mode == Game.Modes.MISSION_BUILDER)
            {
                if (!editor.saveShip(() => { }))
                    return McpProtocol.ToolError(
                        "The game showed a dialog instead of saving. Take a screenshot to see it."
                    );
            }
            else
            {
                // The save (or overwrite confirmation) path of saveShip.
                editor.onSaveConfirm();
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["name"] = editor.ship.shipName,
                    ["path"] = RelativeToRoot(path),
                    ["overwritten"] = overwriting,
                }
            );
        }

        McpProtocol.ToolCallResult SetShipInfo(SetShipInfoParams args)
        {
            string error = CheckEditor(out var editor, requireIdle: false);
            if (error != null)
                return McpProtocol.ToolError(error);
            if (args.Name == null && args.Description == null)
                return McpProtocol.ToolError("Specify name and/or description.");
            if (args.Name != null && string.IsNullOrWhiteSpace(args.Name))
                return McpProtocol.ToolError("The name must not be empty.");

            ApplyShipInfo(editor, args.Name, args.Description);
            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["name"] = editor.shipNameField.text,
                    ["description"] = editor.shipDescriptionField.text,
                }
            );
        }

        static void ApplyShipInfo(EditorLogic editor, string name, string description)
        {
            if (name != null)
            {
                // Setting the text runs the field's length limit.
                editor.shipNameField.text = name;
                editor.onShipNameFieldSubmit(editor.shipNameField.text);
            }
            if (description != null)
            {
                editor.shipDescriptionField.text = description;
                editor.ship.shipDescription = description;
            }
        }

        IEnumerator Launch(LaunchParams args, Action<McpProtocol.ToolCallResult> callback)
        {
            return _sceneTools.RunSceneChange(
                "Launching",
                args.TimeoutSeconds,
                () =>
                {
                    string error = CheckEditor(out var editor);
                    if (error != null)
                        return error;
                    if (!HighLogic.CurrentGame.Parameters.Editor.CanLaunch)
                        return "Launching is disabled for this game.";
                    if (editor.ship.parts.Count == 0)
                        return "There is no ship to launch.";

                    string site = editor.launchSiteName;
                    if (!string.IsNullOrEmpty(args.LaunchSite))
                    {
                        site = EditorDriver.ValidLaunchSites.FirstOrDefault(s =>
                            string.Equals(s, args.LaunchSite, StringComparison.OrdinalIgnoreCase)
                        );
                        if (site == null)
                            return $"Unknown or unavailable launch site '{args.LaunchSite}'. Available: {string.Join(", ", EditorDriver.ValidLaunchSites)}.";
                    }

                    editor.launchVessel(site);
                    return null;
                },
                callback,
                // Pre-flight checks and the launch itself run over a few frames.
                requestGraceSeconds: 3f
            );
        }

        #endregion

        #region JSON Helpers

        // Decimals, because .NET Framework prints most doubles with 17 digits.
        static JsonNode R(double value, int digits = 3) =>
            double.IsNaN(value) || double.IsInfinity(value)
                ? null
                : JsonValue.Create((decimal)Math.Round(value, digits));

        static JsonObject Vec(Vector3 v, int digits = 3) =>
            new JsonObject
            {
                ["x"] = R(v.x, digits),
                ["y"] = R(v.y, digits),
                ["z"] = R(v.z, digits),
            };

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s;
            return s.Substring(0, max) + "...";
        }

        #endregion
    }
}
