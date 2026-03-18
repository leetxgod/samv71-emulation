using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class SAMV71_EEFC : BasicDoubleWordPeripheral, IKnownSize
    {
        public SAMV71_EEFC(IMachine machine, IMemory underlyingMemory,
            uint flashIdentifier   = DefaultFlashIdentifier,
            int  pageSize          = DefaultPageSize,
            int  lockRegionSize    = DefaultLockRegionSize) : base(machine)
        {
            if(underlyingMemory == null)
                throw new ConstructionException($"'{nameof(underlyingMemory)}' can't be null");

            if(underlyingMemory.Size % pageSize > 0)
                throw new ConstructionException($"Memory size not divisible by page size ({pageSize})");

            if(lockRegionSize % pageSize > 0)
                throw new ConstructionException($"Lock region size ({lockRegionSize}) not divisible by page size ({pageSize})");

            this.underlyingMemory = underlyingMemory;
            this.flashIdentifier  = flashIdentifier;
            this.pageSize         = pageSize;
            this.lockRegionSize   = lockRegionSize;
            this.lockBits         = new bool[NumberOfLockRegions];

            // GPNVM1 defaults to 1 (boot from flash) so firmware runs in emulation
            gpnvmBits = new bool[NumberOfGPNVMBits];
            gpnvmBits[1] = true;

            DefineRegisters();
        }

        public long Size => 0x400;
        public string Version => "v6-setlockbit-oob";

        public GPIO IRQ { get; } = new GPIO();

        public int NumberOfPages       => (int)(underlyingMemory.Size / pageSize);
        public int NumberOfLockRegions => (int)(underlyingMemory.Size / lockRegionSize);
        public int PagesPerLockRegion  => lockRegionSize / pageSize;

        public void EraseAll()
        {
            for(var i = 0; i < NumberOfPages; i++)
                ErasePage(i);
        }

        public void ErasePage(int page)
        {
            if(page < 0 || page >= NumberOfPages)
                throw new RecoverableException($"Page {page} out of range (0-{NumberOfPages - 1})");

            if(IsPageLocked(page))
            {
                lockeError = true;
                this.Log(LogLevel.Debug, "Tried to erase locked page #{0}", page);
                return;
            }

            underlyingMemory.WriteBytes(
                page * pageSize,
                Enumerable.Repeat((byte)0xFF, pageSize).ToArray(),
                0, pageSize);
        }

        private int  PageToLockRegion(int page) => page * pageSize / lockRegionSize;
        private bool IsPageLocked(int page)      => lockBits[PageToLockRegion(page)];

        private static string GPNVMBitName(int bit)
        {
            switch(bit)
            {
                case 0: return "Security bit";
                case 1: return "Boot mode (0=ROM, 1=Flash)";
                case 2: return "TCM configuration";
                default: return "Unknown";
            }
        }

        private void ExecuteCommand(Commands command, int argument)
        {
            this.Log(LogLevel.Debug, "EEFC command {0}, arg 0x{1:X}", command, argument);

            IRQ.Unset();
            // Reset error flags at start of every command — plain bools, not register fields
            cmdError  = false;
            lockeError = false;

            switch(command)
            {
            case Commands.GetFlashDescriptor:
                resultQueue.EnqueueRange(new uint[]
                {
                    flashIdentifier,
                    (uint)underlyingMemory.Size,
                    (uint)pageSize,
                    1,
                    (uint)underlyingMemory.Size,
                    (uint)NumberOfLockRegions
                });
                resultQueue.EnqueueRange(
                    Enumerable.Repeat((uint)lockRegionSize, NumberOfLockRegions));
                break;

            case Commands.WritePage:
                break;

            case Commands.WritePageAndLock:
                if(argument >= NumberOfPages) { cmdError = true; return; }
                lockBits[PageToLockRegion(argument)] = true;
                this.Log(LogLevel.Debug, "WritePageAndLock: locked region #{0}", PageToLockRegion(argument));
                break;

            case Commands.ErasePageAndWritePage:
                if(argument >= NumberOfPages) { cmdError = true; return; }
                ErasePage(argument);
                break;

            case Commands.ErasePageAndWritePageThenLock:
                if(argument >= NumberOfPages) { cmdError = true; return; }
                ErasePage(argument);
                lockBits[PageToLockRegion(argument)] = true;
                break;

            case Commands.EraseAll:
                EraseAll();
                break;

            case Commands.ErasePages:
                var countSel  = argument & 0x3;
                var pageCount = 4 << countSel;
                var pageStart = (argument >> (2 + countSel)) * pageCount;
                this.Log(LogLevel.Debug, "ErasePages: {0} pages from {1}", pageCount, pageStart);
                for(var i = 0; i < pageCount && pageStart + i < NumberOfPages; i++)
                    ErasePage(pageStart + i);
                break;

            case Commands.SetLockBit:
                if(argument >= NumberOfPages)
                {
                    // Firmware may scan one past the end; ignore silently
                    this.Log(LogLevel.Debug, "SetLockBit: page {0} out of range, ignoring", argument);
                    break;
                }
                lockBits[PageToLockRegion(argument)] = true;
                this.Log(LogLevel.Debug, "Locked region #{0}", PageToLockRegion(argument));
                break;

            case Commands.ClearLockBit:
                if(argument >= NumberOfPages)
                {
                    // Firmware may scan one past the end; ignore silently
                    this.Log(LogLevel.Debug, "ClearLockBit: page {0} out of range, ignoring", argument);
                    break;
                }
                lockBits[PageToLockRegion(argument)] = false;
                this.Log(LogLevel.Debug, "Unlocked region #{0}", PageToLockRegion(argument));
                break;

            case Commands.GetLockBit:
                if(argument >= NumberOfPages)
                {
                    // Firmware may scan one past the end; return 0 (unlocked) silently
                    this.Log(LogLevel.Debug, "GetLockBit: page {0} out of range, returning 0", argument);
                    resultQueue.Enqueue(0U);
                    break;
                }
                resultQueue.Enqueue(lockBits[PageToLockRegion(argument)] ? 1U : 0U);
                break;

            case Commands.EraseSector:
                // FARG is a page number, not a sector index — convert
                var sectorIndex = argument / PagesPerLockRegion;
                if(sectorIndex >= NumberOfLockRegions) { cmdError = true; return; }
                if(lockBits[sectorIndex])
                {
                    lockeError = true;
                    this.Log(LogLevel.Debug, "Tried to erase locked sector #{0}", sectorIndex);
                    break;
                }
                var sectorFirstPage = sectorIndex * PagesPerLockRegion;
                this.Log(LogLevel.Debug, "EraseSector #{0}: pages {1}-{2}",
                    sectorIndex, sectorFirstPage, sectorFirstPage + PagesPerLockRegion - 1);
                for(var i = 0; i < PagesPerLockRegion; i++)
                    ErasePage(sectorFirstPage + i);
                break;

            case Commands.SetGPNVM:
                if(argument > MaxGPNVMArgument) break;
                if(argument < NumberOfGPNVMBits)
                {
                    gpnvmBits[argument] = true;
                    this.Log(LogLevel.Info, "GPNVM bit {0} set ({1})", argument, GPNVMBitName(argument));
                }
                break;

            case Commands.ClearGPNVM:
                if(argument > MaxGPNVMArgument) break;
                if(argument < NumberOfGPNVMBits)
                {
                    gpnvmBits[argument] = false;
                    this.Log(LogLevel.Info, "GPNVM bit {0} cleared ({1})", argument, GPNVMBitName(argument));
                }
                break;

            case Commands.GetGPNVM:
                uint gpnvmWord = 0;
                for(var i = 0; i < NumberOfGPNVMBits; i++)
                    if(gpnvmBits[i]) gpnvmWord |= (1u << i);
                resultQueue.Enqueue(gpnvmWord);
                this.Log(LogLevel.Debug, "GetGPNVM returning 0x{0:X8}", gpnvmWord);
                break;

            case Commands.StartReadUniqueIdentifier:
            case Commands.StopReadUniqueIdentifier:
            case Commands.GetCALIB:
            case Commands.WriteUserSignature:
            case Commands.EraseUserSignature:
            case Commands.StartReadUserSignature:
            case Commands.StopReadUserSignature:
                this.Log(LogLevel.Warning, "Command {0} not implemented", command);
                break;

            default:
                this.Log(LogLevel.Error, "Unknown EEFC command 0x{0:X}", (int)command);
                cmdError = true;
                break;
            }
        }

        private void DefineRegisters()
        {
            // EEFC_FMR — Flash Mode Register (0x00)
            Registers.Mode.Define(this)
                .WithFlag(0, out generateInterrupt, name: "FRDY",
                    changeCallback: (_, __) => IRQ.Set(generateInterrupt.Value))
                .WithTag("FWS",  8, 4)
                .WithTag("SCOD", 16, 1)
                .WithTag("FAM",  24, 1)
                .WithTag("CLOE", 26, 1)
                .WithReservedBits(27, 5)
            ;

            // EEFC_FCR — Flash Command Register (0x04)
            Registers.Command.Define(this)
                .WithValueField(0,  8,  out var cmd, name: "FCMD")
                .WithValueField(8,  16, out var arg, name: "FARG")
                .WithValueField(24, 8,  out var key, name: "FKEY")
                .WithWriteCallback((_, __) =>
                {
                    if(key.Value != CommandKey)
                    {
                        this.Log(LogLevel.Warning, "Invalid EEFC key 0x{0:X}", key.Value);
                        cmdError = true;
                        return;
                    }
                    if(!KnownCommands.Contains((int)cmd.Value))
                    {
                        this.Log(LogLevel.Warning, "Unknown EEFC command 0x{0:X}", cmd.Value);
                        cmdError = true;
                        return;
                    }
                    ExecuteCommand((Commands)cmd.Value, (int)arg.Value);
                    IRQ.Set(generateInterrupt.Value);
                })
            ;

            // EEFC_FSR — Flash Status Register (0x08)
            // FRDY always 1 (flash is instantaneous in emulation)
            // FCMDE and FLOCKE read from plain bool fields, cleared on next command
            Registers.Status.Define(this)
                .WithFlag(0, FieldMode.Read, name: "FRDY",
                    valueProviderCallback: _ => true)
                .WithFlag(1, FieldMode.Read, name: "FCMDE",
                    valueProviderCallback: _ => cmdError)
                .WithFlag(2, FieldMode.Read, name: "FLOCKE",
                    valueProviderCallback: _ => lockeError)
                .WithFlag(3, FieldMode.Read, name: "FLERR",
                    valueProviderCallback: _ => false)
                .WithReservedBits(4, 28)
            ;

            // EEFC_FRR — Flash Result Register (0x0C)
            Registers.Result.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "FRR",
                    valueProviderCallback: _ =>
                        resultQueue.TryDequeue(out var result) ? result : 0)
            ;

            // Firmware polls an extended EFC window at 0x238.
            // Keep a benign R/W stub here to avoid noisy unhandled-access logs.
            Registers.ResultExt.Define(this)
                .WithValueField(0, 32, name: "FRR_EXT",
                    writeCallback: (_, value) => resultExt = (uint)value,
                    valueProviderCallback: _ => resultExt)
            ;
        }

        // ----------------------------------------------------------------
        // Fields
        // ----------------------------------------------------------------

        private IFlagRegisterField generateInterrupt;

        // Plain bools instead of register fields — avoids IFlagRegisterField
        // set/clear timing issues with Renode's register model
        private bool cmdError;
        private bool lockeError;

        private readonly IMemory     underlyingMemory;
        private readonly uint        flashIdentifier;
        private readonly int         pageSize;
        private readonly int         lockRegionSize;
        private readonly bool[]      lockBits;
        private readonly bool[]      gpnvmBits;
        private readonly Queue<uint> resultQueue = new Queue<uint>();
        private uint resultExt;

        private const uint CommandKey             = 0x5A;
        private const uint DefaultFlashIdentifier = 0xA1220E00;
        private const int  DefaultPageSize        = 512;
        private const int  DefaultLockRegionSize  = 8 * 1024;   // 8KB — matches firmware LOCK SIZE
        private const int  NumberOfGPNVMBits      = 3;
        private const int  MaxGPNVMArgument       = 8;

        private static readonly HashSet<int> KnownCommands = new HashSet<int>
        {
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
            0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15,
        };

        private enum Commands
        {
            GetFlashDescriptor            = 0x00,
            WritePage                     = 0x01,
            WritePageAndLock              = 0x02,
            ErasePageAndWritePage         = 0x03,
            ErasePageAndWritePageThenLock = 0x04,
            EraseAll                      = 0x05,
            ErasePages                    = 0x07,
            SetLockBit                    = 0x08,
            ClearLockBit                  = 0x09,
            GetLockBit                    = 0x0A,
            SetGPNVM                      = 0x0B,
            ClearGPNVM                    = 0x0C,
            GetGPNVM                      = 0x0D,
            StartReadUniqueIdentifier     = 0x0E,
            StopReadUniqueIdentifier      = 0x0F,
            GetCALIB                      = 0x10,
            EraseSector                   = 0x11,
            WriteUserSignature            = 0x12,
            EraseUserSignature            = 0x13,
            StartReadUserSignature        = 0x14,
            StopReadUserSignature         = 0x15,
        }

        private enum Registers
        {
            Mode    = 0x00,
            Command = 0x04,
            Status  = 0x08,
            Result  = 0x0C,
            ResultExt = 0x238,
        }
    }
}
