using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class SAMV71_TC0 : IDoubleWordPeripheral, IKnownSize,
                              IProvidesRegisterCollection<DoubleWordRegisterCollection>
    {
        public SAMV71_TC0(IMachine machine, uint masterClockFrequency = 149333324)
        {
            this.machine = machine;
            this.masterClockFrequency = masterClockFrequency;
            IRQ = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            DefineRegisters();
            Reset();
        }

        public long Size => 0x40;

        public GPIO IRQ { get; }

        public DoubleWordRegisterCollection RegistersCollection => registers;

        public void Reset()
        {
            running = false;
            cmr = 0;
            emr = 0;
            cv = 0;
            ra = 0;
            rc = 0xFFFF;
            sr = 0;
            imr = 0;
            scheduleGeneration = 0;
            registers.Reset();
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        private void DefineRegisters()
        {
            Registers.CCR.Define(this)
                .WithValueField(0, 32, FieldMode.Write, writeCallback: (_, val) =>
                {
                    if((val & CCR_CLKDIS) != 0)
                    {
                        running = false;
                        ++scheduleGeneration;
                    }
                    if((val & CCR_SWTRG) != 0)
                    {
                        cv = 0;
                    }
                    if((val & CCR_CLKEN) != 0)
                    {
                        running = true;
                    }
                    ReevaluateScheduling();
                }, name: "CCR")
            ;

            Registers.CMR.Define(this)
                .WithValueField(0, 32,
                    writeCallback: (_, val) =>
                    {
                        cmr = (uint)val;
                        ReevaluateScheduling();
                    },
                    valueProviderCallback: _ => cmr,
                    name: "CMR")
            ;

            Registers.CV.Define(this)
                .WithValueField(0, 32, FieldMode.Read,
                    valueProviderCallback: _ => cv,
                    name: "CV")
            ;

            Registers.RA.Define(this)
                .WithValueField(0, 32,
                    writeCallback: (_, val) =>
                    {
                        ra = (uint)val & 0xFFFFu;
                        ReevaluateScheduling();
                    },
                    valueProviderCallback: _ => ra,
                    name: "RA")
            ;

            Registers.RC.Define(this)
                .WithValueField(0, 32,
                    writeCallback: (_, val) =>
                    {
                        rc = (uint)val & 0xFFFFu;
                        ReevaluateScheduling();
                    },
                    valueProviderCallback: _ => rc,
                    name: "RC")
            ;

            Registers.SR.Define(this)
                .WithValueField(0, 32, FieldMode.Read,
                    valueProviderCallback: _ =>
                    {
                        var value = sr | (running ? SR_CLKSTA : 0u);
                        sr = 0;
                        UpdateIRQ();
                        return value;
                    },
                    name: "SR")
            ;

            Registers.IER.Define(this)
                .WithValueField(0, 32, FieldMode.Write,
                    writeCallback: (_, val) =>
                    {
                        imr |= (uint)val & InterruptMask;
                        ReevaluateScheduling();
                        UpdateIRQ();
                    },
                    name: "IER")
            ;

            Registers.IDR.Define(this)
                .WithValueField(0, 32, FieldMode.Write,
                    writeCallback: (_, val) =>
                    {
                        imr &= ~((uint)val & InterruptMask);
                        ReevaluateScheduling();
                        UpdateIRQ();
                    },
                    name: "IDR")
            ;

            Registers.IMR.Define(this)
                .WithValueField(0, 32, FieldMode.Read,
                    valueProviderCallback: _ => imr,
                    name: "IMR")
            ;

            Registers.EMR.Define(this)
                .WithValueField(0, 32,
                    writeCallback: (_, val) =>
                    {
                        emr = (uint)val;
                        ReevaluateScheduling();
                    },
                    valueProviderCallback: _ => emr,
                    name: "EMR")
            ;
        }

        private void ReevaluateScheduling()
        {
            ++scheduleGeneration;
            if(!running || (imr & InterruptMask) == 0u)
            {
                return;
            }

            if(!TryGetTimerClockHz(out var timerClockHz))
            {
                return;
            }

            if(!TryGetNextEvent(out var ticksUntilEvent, out var eventMask))
            {
                return;
            }

            var delayUs = (ticksUntilEvent * 1000000UL + timerClockHz - 1UL) / timerClockHz;
            if(delayUs == 0)
            {
                delayUs = 1;
            }

            var generation = scheduleGeneration;
            machine.ScheduleAction(TimeInterval.FromMicroseconds(delayUs), _ =>
            {
                if(!running || generation != scheduleGeneration)
                {
                    return;
                }

                AdvanceCounter(ticksUntilEvent);
                var flags = 0u;
                if((eventMask & SR_CPAS) != 0 && cv == ra)
                {
                    flags |= SR_CPAS;
                }
                if((eventMask & SR_CPCS) != 0 && cv == rc)
                {
                    flags |= SR_CPCS;
                    // Waveform UP_RC: reset counter after RC compare.
                    if(IsWaveformMode() && IsWaveformUpRc())
                    {
                        cv = 0;
                    }
                }

                if(flags != 0u)
                {
                    sr |= flags;
                    UpdateIRQ();
                }

                ReevaluateScheduling();
            });
        }

        private bool TryGetTimerClockHz(out ulong timerClockHz)
        {
            var tccLks = cmr & CMR_TCCLKS_MASK;
            switch(tccLks)
            {
                case CMR_TCCLKS_TIMER_CLOCK1:
                    // For this project we treat TIMER_CLOCK1 as MCK or MCK/2,
                    // depending on NODIVCLK, matching Harmony expectations.
                    timerClockHz = ((emr & EMR_NODIVCLK) != 0u)
                        ? masterClockFrequency
                        : masterClockFrequency / 2u;
                    return timerClockHz != 0u;
                case CMR_TCCLKS_TIMER_CLOCK2:
                    timerClockHz = masterClockFrequency / 8u;
                    return timerClockHz != 0u;
                case CMR_TCCLKS_TIMER_CLOCK3:
                    timerClockHz = masterClockFrequency / 32u;
                    return timerClockHz != 0u;
                case CMR_TCCLKS_TIMER_CLOCK4:
                    timerClockHz = masterClockFrequency / 128u;
                    return timerClockHz != 0u;
                case CMR_TCCLKS_TIMER_CLOCK5:
                    timerClockHz = SlowClockHz;
                    return true;
                default:
                    // External clocks XC0/1/2 are not modeled here.
                    timerClockHz = 0u;
                    return false;
            }
        }

        private bool TryGetNextEvent(out ulong ticksUntilEvent, out uint eventMask)
        {
            ticksUntilEvent = ulong.MaxValue;
            eventMask = 0u;

            var period = (rc & 0xFFFFu) + 1u;
            if(period == 0u)
            {
                period = 1u;
            }

            if((imr & SR_CPAS) != 0u && ra <= rc)
            {
                var t = DistanceToValue(cv, ra, period);
                if(t < ticksUntilEvent)
                {
                    ticksUntilEvent = t;
                    eventMask = SR_CPAS;
                }
                else if(t == ticksUntilEvent)
                {
                    eventMask |= SR_CPAS;
                }
            }

            if((imr & SR_CPCS) != 0u)
            {
                var t = DistanceToValue(cv, rc, period);
                if(t < ticksUntilEvent)
                {
                    ticksUntilEvent = t;
                    eventMask = SR_CPCS;
                }
                else if(t == ticksUntilEvent)
                {
                    eventMask |= SR_CPCS;
                }
            }

            return eventMask != 0u && ticksUntilEvent != ulong.MaxValue;
        }

        private static ulong DistanceToValue(uint currentValue, uint targetValue, uint period)
        {
            if(targetValue > currentValue)
            {
                return targetValue - currentValue;
            }
            if(targetValue < currentValue)
            {
                return period - (currentValue - targetValue);
            }

            // If already at compare value, next event is on next cycle.
            return period;
        }

        private void AdvanceCounter(ulong ticks)
        {
            var period = (rc & 0xFFFFu) + 1u;
            if(period == 0u)
            {
                period = 1u;
            }
            cv = (uint)((cv + ticks) % period);
        }

        private bool IsWaveformMode()
        {
            return (cmr & CMR_WAVE) != 0u;
        }

        private bool IsWaveformUpRc()
        {
            return (cmr & CMR_WAVSEL_MASK) == CMR_WAVSEL_UP_RC;
        }

        private void UpdateIRQ()
        {
            IRQ.Set((sr & imr & InterruptMask) != 0u);
        }

        private readonly IMachine machine;
        private readonly DoubleWordRegisterCollection registers;
        private readonly uint masterClockFrequency;

        private bool running;
        private uint cmr;
        private uint emr;
        private uint cv;
        private uint ra;
        private uint rc;
        private uint sr;
        private uint imr;
        private ulong scheduleGeneration;

        private const uint CCR_CLKEN = 1u << 0;
        private const uint CCR_CLKDIS = 1u << 1;
        private const uint CCR_SWTRG = 1u << 2;

        private const uint CMR_TCCLKS_MASK = 0x7u;
        private const uint CMR_TCCLKS_TIMER_CLOCK1 = 0u;
        private const uint CMR_TCCLKS_TIMER_CLOCK2 = 1u;
        private const uint CMR_TCCLKS_TIMER_CLOCK3 = 2u;
        private const uint CMR_TCCLKS_TIMER_CLOCK4 = 3u;
        private const uint CMR_TCCLKS_TIMER_CLOCK5 = 4u;
        private const uint CMR_WAVE = 1u << 15;
        private const uint CMR_WAVSEL_MASK = 0x3u << 13;
        private const uint CMR_WAVSEL_UP_RC = 0x2u << 13;
        private const uint EMR_NODIVCLK = 1u << 8;

        private const ulong SlowClockHz = 32768u;

        private const uint SR_CPAS = 1u << 2;
        private const uint SR_CPCS = 1u << 4;
        private const uint SR_CLKSTA = 1u << 16;
        private const uint InterruptMask = SR_CPAS | SR_CPCS;

        private enum Registers : long
        {
            CCR = 0x00,
            CMR = 0x04,
            CV = 0x10,
            RA = 0x14,
            RC = 0x1C,
            SR = 0x20,
            IER = 0x24,
            IDR = 0x28,
            IMR = 0x2C,
            EMR = 0x30,
        }
    }
}
