using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SD;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SD
{
    public class SAMV71_HSMCI : NullRegistrationPointPeripheralContainer<SDCard>,
                                 IDoubleWordPeripheral, IKnownSize,
                                 IProvidesRegisterCollection<DoubleWordRegisterCollection>
    {
        public SAMV71_HSMCI(IMachine machine) : base(machine)
        {
            sysbus    = machine.GetSystemBus(this);
            IRQ       = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            DefineRegisters();
            Reset();
        }

        public GPIO IRQ  { get; }
        // well done renode, cant align correctly so I am forced to shit the bed
        public long Size => 0x4000;
        public DoubleWordRegisterCollection RegistersCollection => registers;

        public override void Reset()
        {
            sr      = SR_CMDRDY | SR_NOTBUSY | SR_TXRDY | SR_XFRDONE;
            imr     = 0u;
            argr    = 0u;
            blkr    = DefaultBlockLen << 16;
            cardState = CardStateIdle;
            rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;
            registers?.Reset();
            IRQ?.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= FifoBase && offset < FifoBase + FifoSize)
                return 0u;
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= FifoBase && offset < FifoBase + FifoSize)
                return;
            registers.Write(offset, value);
        }

        private void UpdateIRQ() => IRQ.Set((sr & imr) != 0u);

        private void SetIdleStatus()
        {
            sr |= SR_CMDRDY | SR_NOTBUSY | SR_TXRDY | SR_XFRDONE;
        }

        private void SignalResponseTimeout()
        {
            sr |= SR_RTOE;
            SetIdleStatus();
            UpdateIRQ();
        }

        private void ExecuteCommand(uint cmdnb, uint rsptyp, uint spcmd,
                                    uint trcmd, uint trtyp, uint trdir)
        {
            this.DebugLog("HSMCI CMD{0} spcmd={1} rsptyp={2} trcmd={3} trdir={4} trtyp={5}",
                cmdnb, spcmd, rsptyp, trcmd, trdir, trtyp);

            sr &= ~(SR_CMDRDY | SR_BLKE | SR_XFRDONE | SR_RXRDY | SR_NOTBUSY |
                    SR_RINDE | SR_RDIRE | SR_RCRCE | SR_RENDE | SR_RTOE |
                    SR_DCRCE | SR_DTOE | SR_OVRE | SR_UNRE);

            if(spcmd == SPCMD_INIT || spcmd == SPCMD_SYNC)
            {
                SetIdleStatus();
                UpdateIRQ();
                return;
            }

            if(RegisteredPeripheral == null)
            {
                this.WarningLog("CMD{0} issued but no SD card attached", cmdnb);
                rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;
                if(rsptyp == RSPTYP_NONE)
                {
                    SetIdleStatus();
                    UpdateIRQ();
                }
                else
                {
                    SignalResponseTimeout();
                }
                return;
            }

            if(cmdnb == CMD_IO_SEND_OP_COND || cmdnb == CMD_IO_RW_DIRECT || cmdnb == CMD_IO_RW_EXTENDED)
            {
                this.DebugLog("CMD{0} is SDIO-specific; signaling timeout for SD memory card", cmdnb);
                rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;
                if(rsptyp == RSPTYP_NONE)
                {
                    SetIdleStatus();
                    UpdateIRQ();
                }
                else
                {
                    SignalResponseTimeout();
                }
                return;
            }

            var backendArg = argr;
            if(cmdnb == CMD_SELECT_DESELECT_CARD && (backendArg & 0xFFFFu) == 0u && (backendArg >> 16) != 0u)
            {
                backendArg >>= 16;
            }
            var response = RegisteredPeripheral.HandleCommand(cmdnb, backendArg)?.AsByteArray();
            if(response == null)
            {
                this.DebugLog("CMD{0} response: <null>", cmdnb);
            }
            else
            {
                this.DebugLog("CMD{0} response length={1} bytes={2}", cmdnb, response.Length, BitConverter.ToString(response));
            }

            if(rsptyp != RSPTYP_NONE && (response == null || response.Length == 0))
            {
                rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;
                SignalResponseTimeout();
                return;
            }

            if(rsptyp == RSPTYP_48BIT || rsptyp == RSPTYP_R1B)
            {
                if(response != null && response.Length >= 4)
                {
                    var s = response.Length - 4;
                    rspr[0] = (uint)(response[s + 0] << 24 | response[s + 1] << 16 |
                                     response[s + 2] << 8  | response[s + 3]);
                }
                else
                {
                    rspr[0] = 0u;
                }
                rspr[1] = rspr[2] = rspr[3] = 0u;

                if(cmdnb == CMD_SEND_IF_COND)
                {
                    var expected = argr & 0x1FFu;
                    var got = rspr[0] & 0x1FFu;
                    if(got != expected)
                    {
                        this.DebugLog("CMD8 malformed response 0x{0:X8} (expected low bits 0x{1:X3}); forcing timeout", rspr[0], expected);
                        rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;
                        SignalResponseTimeout();
                        return;
                    }
                }

                if((rspr[0] & 0xFFFFu) == 0u && ((rspr[0] >> 16) & 0x1FFFu) != 0u)
                {
                    rspr[0] >>= 16;
                }

                // CMD7 select/deselect ?????
                this.DebugLog("cringe moment: {0}", cmdnb);
                if(cmdnb == CMD_APP_CMD || cmdnb == CMD_SEND_STATUS)
                {
                    rspr[0] &= ~CardStatusStateMask;
                    rspr[0] |= (cardState << (int)CardStatusStateShift);
                    rspr[0] |= CardStatusReadyForData;
                    rspr[0] &= ~(1u << 25); // clear LOCKED bit
                    if(cmdnb == CMD_APP_CMD)
                    {
                        rspr[0] |= CardStatusAppCmd;
                    }
                }
            }
            else if(rsptyp == RSPTYP_136BIT)
            {
                if(response != null && response.Length >= 16)
                {
                    var s = response.Length - 16;
                    for(var i = 0; i < 4; i++)
                        rspr[i] = (uint)(response[s + 0 + i * 4] << 24 |
                                         response[s + 1 + i * 4] << 16 |
                                         response[s + 2 + i * 4] << 8  |
                                         response[s + 3 + i * 4]);
                }
                else
                    rspr[0] = rspr[1] = rspr[2] = rspr[3] = 0u;

                if(cmdnb == CMD_SEND_CSD)
                {
                    SynthesizeCsdResponseForHarmony();
                }
            }
            
            if(trcmd == TRCMD_START)
            {
                var blkLen = (blkr >> 16) & 0xFFFFu;  // BLKLEN at [31:16]
                var blkCnt =  blkr        & 0xFFFFu;  // BCNT   at [15:0]
                if(blkLen == 0u) blkLen = DefaultBlockLen;
                if(blkCnt == 0u) blkCnt = 1u;

                if(trdir == TRDIR_READ)
                    DoDataRead(blkLen * blkCnt);
                else
                    DoDataWrite(blkLen * blkCnt);
            }
            else
            {
                SetIdleStatus();
                UpdateIRQ();
            }

            // set states
            if(cmdnb == CMD_GO_IDLE_STATE)
            {
                cardState = CardStateIdle;
            }
            else if(cmdnb == CMD_SEND_RELATIVE_ADDR)
            {
                cardState = CardStateStandby;
            }
            else if(cmdnb == CMD_SELECT_DESELECT_CARD)
            {
                cardState = (argr == 0u) ? CardStateStandby : CardStateTransfer;
            }
        }

        private void DoDataRead(uint totalBytes)
        {
            var data = RegisteredPeripheral.ReadData(totalBytes);

            var cda = (ulong)sysbus.ReadDoubleWord(XDMAC_CH0_CDA);
            this.DebugLog("DoDataRead: {0} bytes -> RAM 0x{1:X8}", data.Length, cda);

            if(cda == 0u)
                this.WarningLog("DoDataRead: XDMAC CDA is 0 -- XDMAC stub may not be in .repl");
            else
                sysbus.WriteBytes(data, cda, 0, data.Length);

            sr |= SR_CMDRDY | SR_BLKE | SR_XFRDONE | SR_NOTBUSY | SR_TXRDY;
            UpdateIRQ();
        }

        private void DoDataWrite(uint totalBytes)
        {
            var csa = (ulong)sysbus.ReadDoubleWord(XDMAC_CH0_CSA);
            this.DebugLog("DoDataWrite: {0} bytes from RAM 0x{1:X8}", totalBytes, csa);

            if(csa == 0u)
            {
                this.WarningLog("DoDataWrite: XDMAC CSA is 0 -- XDMAC stub may not be in .repl");
            }
            else
            {
                var data = sysbus.ReadBytes(csa, (int)totalBytes);
                RegisteredPeripheral.WriteData(data);
            }

            sr |= SR_CMDRDY | SR_BLKE | SR_XFRDONE | SR_NOTBUSY | SR_TXRDY;
            UpdateIRQ();
        }

        private void SynthesizeCsdResponseForHarmony()
        {
            // ok
            rspr[0] = 0x00000000u;
            rspr[1] = 0x000903FFu;
            rspr[2] = 0xC0030000u;
            rspr[3] = 0x00000000u;
        }

        private void DefineRegisters()
        {
            // CR — Control Register (0x00)
            Registers.CR.Define(this)
                .WithValueField(0, 32, name: "CR")
            ;

            // MR — Mode Register (0x04)
            Registers.MR.Define(this)
                .WithValueField(0, 32, name: "MR")
            ;

            // DTOR — Data Timeout Register (0x08)
            Registers.DTOR.Define(this)
                .WithValueField(0, 32, name: "DTOR")
            ;

            // SDCR — SD/SDIO Card Register (0x0C)
            Registers.SDCR.Define(this)
                .WithValueField(0, 32, name: "SDCR")
            ;

            // ARGR — Argument Register (0x10)
            Registers.ARGR.Define(this)
                .WithValueField(0, 32,
                    writeCallback:         (_, val) => argr = (uint)val,
                    valueProviderCallback: _        => argr,
                    name: "ARG")
            ;

            // CMDR — Command Register (0x14)
            Registers.CMDR.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "CMDR",
                    writeCallback: (_, val) =>
                    {
                        var cmdnb  = (uint)( val        & 0x3Fu);  // [5:0]
                        var rsptyp = (uint)((val >>  6) & 0x3u);   // [7:6]
                        var spcmd  = (uint)((val >>  8) & 0x7u);   // [10:8]
                        // OPDCMD - MAXLAT ignored because I do not care anymore
                        var trcmd  = (uint)((val >> 16) & 0x3u);   // [17:16]
                        var trdir  = (uint)((val >> 18) & 0x1u);   // [18]
                        var trtyp  = (uint)((val >> 19) & 0x7u);   // [21:19]
                        ExecuteCommand(cmdnb, rsptyp, spcmd, trcmd, trtyp, trdir);
                    })
            ;

            // BLKR — Block Register (0x18): [15:0]=BCNT, [31:16]=BLKLEN
            Registers.BLKR.Define(this)
                .WithValueField(0, 32,
                    writeCallback:         (_, val) => blkr = (uint)val,
                    valueProviderCallback: _        => blkr,
                    name: "BLKR")
            ;

            // CSTOR — Completion Signal Timeout Register (0x1C)
            Registers.CSTOR.Define(this)
                .WithValueField(0, 32, name: "CSTOR")
            ;

            // RSPR[0–3] — Response Registers (0x20–0x2C): ro
            Registers.RSPR0.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RSP",
                    valueProviderCallback: _ => rspr[0])
            ;
            Registers.RSPR1.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RSP",
                    valueProviderCallback: _ => rspr[1])
            ;
            Registers.RSPR2.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RSP",
                    valueProviderCallback: _ => rspr[2])
            ;
            Registers.RSPR3.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "RSP",
                    valueProviderCallback: _ => rspr[3])
            ;

            // RDR — Receive Data Register (0x30): DMA handles actual data
            Registers.RDR.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "DATA",
                    valueProviderCallback: _ => 0u)
            ;

            // TDR — Transmit Data Register (0x34): DMA handles actual data
            Registers.TDR.Define(this)
                .WithValueField(0, 32, name: "TDR")
            ;

            // SR — Status Register (0x40): read-only, computed from plain sr field
            Registers.SR.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "SR",
                    valueProviderCallback: _ => sr)
            ;

            // IER — Interrupt Enable Register (0x44): write-only, ORs into imr
            Registers.IER.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "IER",
                    writeCallback: (_, val) => { imr |= (uint)val; UpdateIRQ(); })
            ;

            // IDR — Interrupt Disable Register (0x48): write-only, clears imr bits
            Registers.IDR.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "IDR",
                    writeCallback: (_, val) => { imr &= ~(uint)val; UpdateIRQ(); })
            ;

            // IMR — Interrupt Mask Register (0x4C): read-only
            Registers.IMR.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "IMR",
                    valueProviderCallback: _ => imr)
            ;

            // DMA — DMA Configuration Register (0x50)
            Registers.DMA.Define(this)
                .WithValueField(0, 32, name: "DMA")
            ;

            // CFG — Configuration Register (0x54)
            Registers.CFG.Define(this)
                .WithValueField(0, 32, name: "CFG")
            ;

            // WPMR — Write Protection Mode Register (0xE4)
            Registers.WPMR.Define(this)
                .WithValueField(0, 32, name: "WPMR")
            ;

            // WPSR — Write Protection Status Register (0xE8): always 0
            Registers.WPSR.Define(this)
                .WithValueField(0, 32, FieldMode.Read, name: "WPSR",
                    valueProviderCallback: _ => 0u)
            ;
        }

        private readonly IBusController sysbus;
        private readonly DoubleWordRegisterCollection registers;

        private uint sr;                            // status register (computed)
        private uint imr;                           // interrupt mask register
        private uint argr;                          // command argument
        private uint blkr;                          // block register (BLKLEN | BCNT)
        private uint cardState;                     // card status current_state field [12:9]
        private readonly uint[] rspr = new uint[4]; // response registers

        // FIFO window
        private const long FifoBase = 0x200;
        private const long FifoSize = 0x400;  // 256 × 4-byte words = 0x400 bytes

        // SR status bits
        private const uint SR_CMDRDY  = 1u << 0;  // command ready
        private const uint SR_RXRDY   = 1u << 1;  // receive data ready
        private const uint SR_TXRDY   = 1u << 2;  // transmit holding register empty
        private const uint SR_BLKE    = 1u << 3;  // data block ended
        private const uint SR_NOTBUSY = 1u << 5;  // data line not busy
        private const uint SR_RINDE   = 1u << 16; // response index error
        private const uint SR_RDIRE   = 1u << 17; // response direction error
        private const uint SR_RCRCE   = 1u << 18; // response CRC error
        private const uint SR_RENDE   = 1u << 19; // response end bit error
        private const uint SR_RTOE    = 1u << 20; // response timeout error
        private const uint SR_DCRCE   = 1u << 21; // data CRC error
        private const uint SR_DTOE    = 1u << 22; // data timeout error
        private const uint SR_XFRDONE = 1u << 27; // transfer done
        private const uint SR_OVRE    = 1u << 30; // overrun
        private const uint SR_UNRE    = 1u << 31; // underrun

        // CMDR SPCMD field values [10:8]
        private const uint SPCMD_STD  = 0u;  // standard command
        private const uint SPCMD_INIT = 1u;  // initialization command (74-cycle sequence)
        private const uint SPCMD_SYNC = 2u;  // synchronized command

        // CMDR TRCMD field values [17:16]
        private const uint TRCMD_NONE  = 0u;  // no data transfer
        private const uint TRCMD_START = 1u;  // start data transfer

        // CMDR TRDIR field value [18]
        private const uint TRDIR_WRITE = 0u;
        private const uint TRDIR_READ  = 1u;

        // CMDR RSPTYP field values [7:6]
        private const uint RSPTYP_NONE   = 0u;  // no response
        private const uint RSPTYP_48BIT  = 1u;  // 48-bit response (R1/R3/R6/R7)
        private const uint RSPTYP_136BIT = 2u;  // 136-bit response (R2: CID/CSD)
        private const uint RSPTYP_R1B    = 3u;  // R1b (48-bit with busy)

        // XDMAC channel 0 CSA/CDA registers (base 0x40078000 + channel-0 offset 0x60/0x64)
        private const ulong XDMAC_CH0_CSA = 0x40078060UL;  // source address (write: RAM → FIFO)
        private const ulong XDMAC_CH0_CDA = 0x40078064UL;  // dest   address (read:  FIFO → RAM)

        private const uint DefaultBlockLen = 512u;
        private const uint CMD_GO_IDLE_STATE = 0u;
        private const uint CMD_SEND_RELATIVE_ADDR = 3u;
        private const uint CMD_SEND_CSD = 9u;
        private const uint CMD_SEND_STATUS = 13u;
        private const uint CMD_APP_CMD = 55u;
        private const uint CMD_SELECT_DESELECT_CARD = 7u;
        private const uint CMD_SEND_IF_COND = 8u;
        private const uint CMD_IO_SEND_OP_COND = 5u;
        private const uint CMD_IO_RW_DIRECT = 52u;
        private const uint CMD_IO_RW_EXTENDED = 53u;
        private const uint CardStatusAppCmd = 1u << 5;
        private const uint CardStatusReadyForData = 1u << 8;
        private const uint CardStatusStateShift = 9u;
        private const uint CardStatusStateMask = 0xFu << (int)CardStatusStateShift;
        private const uint CardStateIdle = 0u;
        private const uint CardStateStandby = 3u;
        private const uint CardStateTransfer = 4u;

        private enum Registers : long
        {
            CR    = 0x00,
            MR    = 0x04,
            DTOR  = 0x08,
            SDCR  = 0x0C,
            ARGR  = 0x10,
            CMDR  = 0x14,
            BLKR  = 0x18,
            CSTOR = 0x1C,
            RSPR0 = 0x20,
            RSPR1 = 0x24,
            RSPR2 = 0x28,
            RSPR3 = 0x2C,
            RDR   = 0x30,
            TDR   = 0x34,
            SR    = 0x40,
            IER   = 0x44,
            IDR   = 0x48,
            IMR   = 0x4C,
            DMA   = 0x50,
            CFG   = 0x54,
            WPMR  = 0xE4,
            WPSR  = 0xE8,
        }
    }
}
