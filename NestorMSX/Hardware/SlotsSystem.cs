﻿using System;
using System.Collections.Generic;
using System.Linq;
using Konamiman.NestorMSX.Misc;
using Konamiman.Z80dotNet;

namespace Konamiman.NestorMSX.Hardware
{
    /// <summary>
    /// Class that represents the MSX slots system. For now it supports primary slots only.
    /// </summary>
    public class SlotsSystem : IExternallyControlledSlotsSystem
    {
        private const int PrimarySlotsCount = 4;
        private const int MinSlotPartNumber = 0;
        private const int MaxSlotPartNumber = PrimarySlotsCount - 1;
        
        private bool[] isExpanded = new bool[PrimarySlotsCount];
        private IDictionary<SlotNumber, IMemory> slotContents;
        private SlotNumber[] visibleSlotNumbers;
        private IDictionary<ushort, IMemory> visibleSlotContents;
        private byte slotSelectionRegisterValue;
        private bool slotVisibleInPage3IsExpanded = false;
        private Dictionary<TwinBit, TwinBit[]> secondarySlotsSelectedForEachPrimarySlot;
        private byte[] secondarySlotSelectionRegisterForEachPrimarySlot = new byte[] {0,0,0,0};

        public SlotsSystem() : this(new Dictionary<SlotNumber, IMemory>(), new TwinBit[0])
        {
        }

        public SlotsSystem(TwinBit[] expandedSlots) : this(new Dictionary<SlotNumber, IMemory>(), expandedSlots)
        {
        }

        public SlotsSystem(IDictionary<SlotNumber, IMemory> slotContents)
            : this(slotContents, ExpandedSlotsFromContents(slotContents))
        {
        }

        private static TwinBit[] ExpandedSlotsFromContents(IDictionary<SlotNumber, IMemory> slotContents)
        {
            if (slotContents == null)
                throw new ArgumentNullException("slotContents");

            return Enumerable.Range(0, 4)
                .Select(n => (TwinBit)n)
                .Where(n => slotContents.Keys.Any(sn => sn.PrimarySlotNumber == n && sn.IsExpandedSlot))
                .Distinct()
                .ToArray();
        }

        public SlotsSystem(IDictionary<SlotNumber, IMemory> slotContents, TwinBit[] expandedSlots)
        {
            if(slotContents == null)
                throw new ArgumentNullException("slotContents");

            if(expandedSlots == null)
                throw new ArgumentNullException("expandedSlots");

            if(slotContents.Any(c => c.Value != null && c.Value.Size != Size))
                throw new ArgumentException("Size of slot contents must be 65536 bytes");

            foreach(var slot in expandedSlots) {
                isExpanded[slot] = true;
            }

            if(slotContents.Any(c => c.Key.IsExpandedSlot && !IsExpanded(c.Key.PrimarySlotNumber)))
                throw new ArgumentException("Can't specify content for a subslot if the slot is not expanded");

            this.slotContents = new Dictionary<SlotNumber, IMemory>(slotContents);
            FillMissingSlotContentsWithNotConnectedMemory();
            FillSecondarySlotsSelectedForEachPrimarySlotTable();
            SetAllPagesToSlotZero();
        }

        private void FillSecondarySlotsSelectedForEachPrimarySlotTable()
        {
            secondarySlotsSelectedForEachPrimarySlot = new Dictionary<TwinBit, TwinBit[]>();
            for(int primarySlot = 0; primarySlot < 4; primarySlot++) {
                if(!IsExpanded(primarySlot))
                    continue;

                secondarySlotsSelectedForEachPrimarySlot[primarySlot] = new TwinBit[] {0, 0, 0, 0};
            }
        }

        private void SetAllPagesToSlotZero()
        {
            var slotZero = slotContents.Keys.Single(s => s.PrimarySlotNumber == 0 && s.SubSlotNumber == 0);
            var slotZeroContents = slotContents[slotZero];
            visibleSlotNumbers = new[] { slotZero, slotZero, slotZero, slotZero };

            visibleSlotContents = new Dictionary<ushort, IMemory>();
            for(int page = 0; page < 4; page++) {
                visibleSlotContents[((Z80Page)page).AddressMask] = slotZeroContents;
            }

            slotVisibleInPage3IsExpanded = isExpanded[0];
            slotSelectionRegisterValue = 0;
        }

        private void FillMissingSlotContentsWithNotConnectedMemory()
        {
            for(byte primary = MinSlotPartNumber; primary <= MaxSlotPartNumber; primary++) {
                if(isExpanded[primary]) {
                    for(int secondary = MinSlotPartNumber; secondary <= MaxSlotPartNumber; secondary++) {
                        var slotNumber = new SlotNumber(primary, secondary);
                        if(!ThereIsContentForSlot(slotNumber))
                            slotContents[slotNumber] = NotConnectedMemory.Value;
                    }
                }
                else if(!ThereIsContentForSlot(primary)) {
                    slotContents[primary] = NotConnectedMemory.Value;
                }
            }
        }

        bool ThereIsContentForSlot(SlotNumber slot)
        {
            return slotContents.ContainsKey(slot) && slotContents[slot] != null;
        }

        public int Size
        {
            get
            {
                return ushort.MaxValue + 1;
            }
        }

        public byte this[int address]
        {
            get
            {
                return address == 0xFFFF && slotVisibleInPage3IsExpanded ?
                    (byte)~secondarySlotSelectionRegisterForEachPrimarySlot[visibleSlotNumbers[3].PrimarySlotNumber]
                    : visibleSlotContents[(ushort)(address & 0xC000)][address];
            }
            set
            {
                if(address == 0xFFFF && slotVisibleInPage3IsExpanded)
                    WriteToSecondarySlotRegister(value);
                else
                    visibleSlotContents[(ushort)(address & 0xC000)][address] = value;
            }
        }

        private void WriteToSecondarySlotRegister(byte value)
        {
            var primarySlotNumber = visibleSlotNumbers[3].PrimarySlotNumber;
            secondarySlotSelectionRegisterForEachPrimarySlot[primarySlotNumber] = value;
            var tempValue = value;

            for (int p = 0; p < 4; p++)
            {
                TwinBit page = p;
                TwinBit subslotNumber = tempValue & 3;
                secondarySlotsSelectedForEachPrimarySlot[primarySlotNumber][page] = subslotNumber;

                if(visibleSlotNumbers[page].PrimarySlotNumber == primarySlotNumber)
                {
                    var newSlotNumber = new SlotNumber(primarySlotNumber, subslotNumber);
                    SetVisibleSlot((int)page, newSlotNumber);
                }

                tempValue >>= 2;
            }

            if(SecondarySlotSelectionRegisterWritten != null)
                SecondarySlotSelectionRegisterWritten(
                    this,
                    new SecondarySlotSelectionRegisterWrittenEventArgs(value, primarySlotNumber));
        }

        public void SetContents(int startAddress, byte[] contents, int startIndex = 0, int? length = null)
        {
            var actualLength = length.GetValueOrDefault(contents.Length);
            for(int i = 0; i < actualLength; i++) {
                this[startAddress + i] = contents[i];
            }
        }

        public byte[] GetContents(int startAddress, int length)
        {
            var items = new List<byte>();
            for(int i = startAddress; i < length; i++)
                items.Add(this[i]);

            return items.ToArray();
        }

        public void WriteToSlotSelectionRegister(byte value)
        {
            slotSelectionRegisterValue = value;

            for(int p = 0; p <4; p++) {
                Z80Page page = p;
                var primarySlotNumber = value & 3;
                SlotNumber slotNumber;
                if(isExpanded[primarySlotNumber])
                {
                    var subslotNumber = secondarySlotsSelectedForEachPrimarySlot[primarySlotNumber][page];
                    slotNumber = new SlotNumber(primarySlotNumber, subslotNumber);
                }
                else
                {
                    slotNumber = new SlotNumber((byte)primarySlotNumber);
                }

                SetVisibleSlot(page, slotNumber);
                value >>= 2;
            }

            if(SlotSelectionRegisterWritten != null)
                SlotSelectionRegisterWritten(this, new SlotSelectionRegisterWrittenEventArgs(slotSelectionRegisterValue));
        }

        private void SetVisibleSlot(Z80Page page, SlotNumber slotNumber)
        {
            visibleSlotNumbers[page] = slotNumber;
            visibleSlotContents[page.AddressMask] = slotContents[slotNumber];
            UpdateSlotVisibleInPage3IsExpanded(page, slotNumber);
        }

        private void UpdateSlotVisibleInPage3IsExpanded(Z80Page page, SlotNumber slotNumber)
        {
            if(page == 3)
                slotVisibleInPage3IsExpanded = isExpanded[slotNumber.PrimarySlotNumber];
        }

        public byte ReadSlotSelectionRegister()
        {
            return slotSelectionRegisterValue;
        }

        public event EventHandler<SlotSelectionRegisterWrittenEventArgs> SlotSelectionRegisterWritten;
        public event EventHandler<SecondarySlotSelectionRegisterWrittenEventArgs> SecondarySlotSelectionRegisterWritten;

        public bool IsExpanded(TwinBit primarySlotNumber)
        {
            return isExpanded[primarySlotNumber];
        }

        public void EnableSlot(Z80Page page, SlotNumber slot)
        {
            ThrowIfUndefinedSlot(slot);

            SetVisibleSlot(page, slot);

            byte newSlotSelectionRegisterValue = 0;
            for(var p = 0; p < 4; p++) {
                newSlotSelectionRegisterValue |= (byte)(visibleSlotNumbers[p].PrimarySlotNumber << 2*p);
            }

            slotSelectionRegisterValue = newSlotSelectionRegisterValue;
        }

        private void ThrowIfUndefinedSlot(SlotNumber slot)
        {
            if(!slotContents.ContainsKey(slot))
                throw new InvalidOperationException(string.Format("Slot {0} is not expanded", slot.PrimarySlotNumber));
        }

        public SlotNumber GetCurrentSlot(Z80Page page)
        {
            return visibleSlotNumbers[page];
        }

        public IMemory GetSlotContents(SlotNumber slot)
        {
            ThrowIfUndefinedSlot(slot);

            return slotContents[slot];
        }

        public void SetSlotContents(SlotNumber slot, IMemory contents)
        {
            ThrowIfUndefinedSlot(slot);

            slotContents[slot] = contents ?? NotConnectedMemory.Value;
            UpdateVisibleSlots();
        }

        private void UpdateVisibleSlots()
        {
            for(var page = 0; page < 4; page++) {
                SetVisibleSlot(page, visibleSlotNumbers[page]);
            }
        }
    }
}
