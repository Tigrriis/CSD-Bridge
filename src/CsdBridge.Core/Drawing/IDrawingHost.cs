using System.Collections.Generic;

namespace CsdBridge.Core.Drawing
{
    /// <summary>
    /// The CAD operations Claude's tools are allowed to perform. This is the whole
    /// surface area: Claude cannot run arbitrary commands, LISP or scripts.
    ///
    /// Implementations are called on the CAD application's main thread (see
    /// <see cref="Agent.IMainThreadInvoker"/>) and may throw; the tool dispatcher
    /// turns exceptions into error results that Claude can read and correct.
    /// </summary>
    public interface IDrawingHost
    {
        DrawingInfo GetDrawingInfo();

        EntityListResult ListEntities(EntityQuery query);

        IReadOnlyList<ItemResult> EnsureLayers(IReadOnlyList<LayerSpec> layers);

        IReadOnlyList<ItemResult> CreateEntities(IReadOnlyList<EntitySpec> entities);

        IReadOnlyList<ItemResult> ModifyEntities(IReadOnlyList<ModifyOperation> operations);

        /// <summary>Ask the user to click a point. Returns world coordinates.</summary>
        PickPointResult PickPoint(string prompt);

        /// <summary>Ask the user to select entities.</summary>
        SelectionResult SelectEntities(string prompt);
    }
}
