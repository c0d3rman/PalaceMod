using System.Collections.Generic;

namespace PalaceMod.Builder.Model
{
    /// <summary>
    /// Undo/redo for the editor, by whole-pack snapshots (packs are a few KB, so this is cheap and can't drift out of
    /// sync the way inverse commands can). Call Record("label") just before each change; Undo/Redo return the pack
    /// to show, which is a new object, so rebind the UI to it.
    /// </summary>
    public sealed class EditHistory
    {
        private readonly List<(string label, string json)> undo = [];
        private readonly List<(string label, string json)> redo = [];
        private readonly int limit;

        public DesignPack Current { get; private set; }

        public EditHistory(DesignPack pack, int limit = 200)
        {
            Current = pack;
            this.limit = limit;
        }

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public string UndoLabel => CanUndo ? undo[undo.Count - 1].label : null;
        public string RedoLabel => CanRedo ? redo[redo.Count - 1].label : null;

        /// <summary>Snapshots the current state before a change named label ("Set max HP").</summary>
        public void Record(string label)
        {
            undo.Add((label, PackSerializer.WriteCompact(Current)));
            if (undo.Count > limit) undo.RemoveAt(0);
            redo.Clear();
        }

        public DesignPack Undo()
        {
            if (!CanUndo) return Current;
            var (label, json) = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            redo.Add((label, PackSerializer.WriteCompact(Current)));
            return Current = PackSerializer.Read(json, null);
        }

        public DesignPack Redo()
        {
            if (!CanRedo) return Current;
            var (label, json) = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            undo.Add((label, PackSerializer.WriteCompact(Current)));
            return Current = PackSerializer.Read(json, null);
        }
    }
}
