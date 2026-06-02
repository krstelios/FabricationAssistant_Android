using System;
using FabricationAssistant.Core.UndoRedo;

namespace FabricationAssistant.App.Android.Bom;

/// <summary>
/// Bridges the Core undo seam to the live consolidated-BOM panel via lambdas, so
/// the stable <see cref="SceneContext"/> (created once at startup) can reach a
/// BOM panel that is created on demand. Returns <see cref="BomFilterStateSnapshot.Empty"/>
/// and no-ops when no consolidated panel is open.
/// </summary>
public sealed class AndroidBomFilterStateAccess : IBomFilterStateAccess
{
    private readonly Func<BomFilterStateSnapshot> _snapshot;
    private readonly Action<BomFilterStateSnapshot> _restore;

    public AndroidBomFilterStateAccess(Func<BomFilterStateSnapshot> snapshot, Action<BomFilterStateSnapshot> restore)
    {
        _snapshot = snapshot;
        _restore = restore;
    }

    public BomFilterStateSnapshot Snapshot() => _snapshot();
    public void RestoreSnapshot(BomFilterStateSnapshot snapshot) => _restore(snapshot);
}
