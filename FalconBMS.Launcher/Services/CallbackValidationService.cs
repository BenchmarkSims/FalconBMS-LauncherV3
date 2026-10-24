using FalconBMS.Launcher.Models;
using FalconBMS.Launcher.Models.Legacy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FalconBMS.Launcher.Services;

/// <summary>
/// Finds and removes DX button / POV assignments whose callback no longer exists
/// in the current BMS FULL key files (for example after a BMS update).
///
/// Used by:
///   - the v2 import (LegacyImportService), before the device JSON is written
///   - normal startup (MainViewModel), after the saved JSON has been loaded
///
/// This service never touches the disk and never shows UI. It only reads the
/// catalogs and edits the in-memory device profiles it is given.
/// </summary>
public sealed class CallbackValidationService
{
    private const string ObsoleteReason =
        "This control is no longer available in the current BMS key files.";

    // SimHotasShift and SimHotasPinkyShift are never treated as obsolete.
    // Added as a fallback check so DX Shift controls are never wiped.
    private static readonly HashSet<string> AlwaysValidCallbacks =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SimDoNothing",
            "SimHotasShift",
            "SimHotasPinkyShift"
        };

    /// <summary>
    /// Read-only check. Returns what WOULD be removed, nothing is changed
    /// </summary>
    public List<LegacyImportSkippedItem> FindObsolete(
        IReadOnlyList<KeyCatalog> catalogs,
        IEnumerable<DeviceBindingProfile> devices)
    {
        return Scan(catalogs, devices, remove: false);
    }

    /// <summary>
    /// Removes obsolete assignments from the in-memory device profiles.
    /// Returns what was removed, same format as FindObsolete.
    /// Call this AFTER the backup succeeds.
    /// </summary>
    public List<LegacyImportSkippedItem> RemoveObsolete(
        IReadOnlyList<KeyCatalog> catalogs,
        IEnumerable<DeviceBindingProfile> devices)
    {
        List<LegacyImportSkippedItem> removed =
            Scan(catalogs, devices, remove: true);

        DebugDiagnosticsService.Info(
            $"Obsolete device callback cleanup complete. AssignmentsRemoved={removed.Count}");

        return removed;
    }

    // One code path for both "find" and "remove" so the two can never disagree.
    private static List<LegacyImportSkippedItem> Scan(
        IReadOnlyList<KeyCatalog> catalogs,
        IEnumerable<DeviceBindingProfile> devices,
        bool remove)
    {
        var report = new List<LegacyImportSkippedItem>();

        Dictionary<string, HashSet<string>> validByAircraft =
            BuildValidCallbackSets(catalogs);

        foreach (DeviceBindingProfile device in devices)
        {
            foreach (DeviceAircraftBindingProfile aircraft in device.AircraftProfiles)
            {
                // Catalog missing for this aircraft, so keep everything
                if (!validByAircraft.TryGetValue(
                        aircraft.AircraftProfile,
                        out HashSet<string>? valid))
                {
                    continue;
                }

                // ToList() so we can remove from the real list while looping
                foreach (DeviceButtonBinding button in
                         aircraft.ButtonBindings
                             .Where(b => !IsValid(b.CallbackName, valid))
                             .ToList())
                {
                    report.Add(CreateItem(
                        device,
                        aircraft,
                        DeviceButtonBinding.BuildDisplayText(
                            button.ButtonIndex,
                            button.AssignmentIndex),
                        button.CallbackName));

                    if (remove)
                        aircraft.ButtonBindings.Remove(button);
                }

                foreach (DevicePovBinding pov in
                         aircraft.PovBindings
                             .Where(p => !IsValid(p.CallbackName, valid))
                             .ToList())
                {
                    report.Add(CreateItem(
                        device,
                        aircraft,
                        $"POV{pov.PovIndex + 1} direction {pov.Direction} ({pov.Invoke})",
                        pov.CallbackName));

                    if (remove)
                        aircraft.PovBindings.Remove(pov);
                }
            }
        }

        return report;
    }

    private static LegacyImportSkippedItem CreateItem(
        DeviceBindingProfile device,
        DeviceAircraftBindingProfile aircraft,
        string controlLabel,
        string callbackName)
    {
        // Group removed assignments by device and aircraft profile
        return new LegacyImportSkippedItem
        {
            SourceName = $"{device.ProductName} ({aircraft.AircraftProfile})",
            ControlName = $"{callbackName} - {controlLabel}",
            Reason = ObsoleteReason
        };
    }

    // One set of valid callback names per aircraft, built from the FULL catalogs
    private static Dictionary<string, HashSet<string>> BuildValidCallbackSets(
        IReadOnlyList<KeyCatalog> catalogs)
    {
        var sets = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (KeyCatalog catalog in catalogs)
        {
            if (!sets.TryGetValue(catalog.AircraftProfile, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                sets[catalog.AircraftProfile] = set;
            }

            foreach (KeyCatalogRow row in catalog.Rows)
            {
                if (row.IsCallback)
                    set.Add(row.CallbackName);
            }
        }

        return sets;
    }

    private static bool IsValid(string callbackName, HashSet<string> validCallbacks)
    {
        return string.IsNullOrWhiteSpace(callbackName) ||
               AlwaysValidCallbacks.Contains(callbackName) ||
               validCallbacks.Contains(callbackName);
    }
}