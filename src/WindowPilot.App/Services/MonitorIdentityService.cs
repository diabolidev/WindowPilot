using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowPilot.App.Services;

/// <summary>
/// Ermittelt den Zielmonitor ohne blind auf DISPLAY1/DISPLAY2 zu vertrauen.
/// Wenn der Bildschirm nicht eindeutig identifizierbar ist, wird NICHT
/// auf einen scheinbar ähnlichen anderen Monitor ausgewichen.
/// </summary>
public static class MonitorIdentityService
{
    public static DisplayMonitor? ResolveTarget(
        SavedWindowRule rule, IReadOnlyList<DisplayMonitor> currentMonitors)
    {
        // Neu gespeicherte Layouts: physische Geräteschnittstelle hat Vorrang.
        // Identische oder nicht mehr gefundene Kennungen sind nicht eindeutig.
        if (!string.IsNullOrWhiteSpace(rule.MonitorStableId))
        {
            DisplayMonitor[] physical = currentMonitors
                .Where(m => string.Equals(m.StableDeviceId,
                    rule.MonitorStableId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return physical.Length == 1 ? physical[0] : null;
        }

        // Altbestände: Display-Nummern können sich beim Start ändern.
        // Daher vorrangig über die vollständige Desktop-Koordinate
        // + Arbeitsbereichsgröße gehen, niemals über DISPLAYn allein.
        SavedRectangle? saved = rule.MonitorWorkArea;
        if (saved is null)
            return null;

        DisplayMonitor[] geometric = currentMonitors.Where(m =>
            m.WorkX == saved.X && m.WorkY == saved.Y &&
            m.WorkWidth == saved.Width && m.WorkHeight == saved.Height)
            .ToArray();
        return geometric.Length == 1 ? geometric[0] : null;
    }
}
