using System.Collections.Generic;

namespace CsdBridge.Core.Drawing
{
    /// <summary>
    /// Optional ability of a drawing host: gather existing entities into a new block definition
    /// and insert it where they were. The in-memory host doesn't offer it; BricsCAD does.
    /// </summary>
    public interface IBlockMaker
    {
        /// <summary>
        /// Moves the entities into a new block named <paramref name="name"/> (made unique if taken),
        /// with its base point at <paramref name="basePoint"/>, and inserts one reference there so the
        /// drawing looks the same. The entities keep their own layers inside the block.
        /// </summary>
        BlockResult MakeBlock(IReadOnlyList<string> handles, string name, Point2 basePoint, string? layer);
    }

    public sealed class BlockResult
    {
        /// <summary>The block's final name (a number is added when the name was taken).</summary>
        public string BlockName { get; set; } = "";

        /// <summary>Handle of the inserted block reference.</summary>
        public string Handle { get; set; } = "";

        public int EntityCount { get; set; }
    }
}
