using System;

namespace Artisan.CraftingLists
{
    internal class WorkshopPreset
    {
        public string Name { get; set; } = "";
        public uint SequenceRowId { get; set; }
        public int Loops { get; set; } = 1;
        public bool IncludePrecraft { get; set; }
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
