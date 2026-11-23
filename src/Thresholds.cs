namespace FluentHud
{
    enum Metric { CpuLoad, CpuThread, CpuTemp, GpuLoad, GpuTemp, Memory, Vram }

    // Normal draws in the system accent (rings/bars) or white (text);
    // Caution and Critical switch to the Fluent yellow / red status colors.
    enum Level { Normal, Caution, Critical }

    static class Thresholds
    {
        // value is a percentage for loads/Memory/Vram, and °C for temperatures.
        // NaN means the sensor isn't available on this machine.
        public static Level Classify(Metric metric, double value)
        {
            if (double.IsNaN(value)) return Level.Normal;
            switch (metric)
            {
                // A game is held back by its busiest thread long before the average looks busy,
                // so one saturated thread is the bottleneck signal.
                case Metric.CpuThread: return Above(value, 85, 97);
                // The total only says the whole chip has nothing spare.
                case Metric.CpuLoad: return Above(value, 85, 95);
                // Laptop chips sit in the low 90s under load by design (a Core Ultra 7 155H is rated
                // to 110 °C); desktop chips start throttling from about 95–100 °C.
                case Metric.CpuTemp: return Above(value, 95, 100);
                // 99% is the goal in a game: the GPU, not the CPU, is setting the frame rate.
                case Metric.GpuLoad: return Level.Normal;
                // NVIDIA's GPU Boost starts giving up clock speed in the low-to-mid 80s.
                case Metric.GpuTemp: return Above(value, 83, 90);
                // Running out is what stutters: Windows pages to disk, games stream textures over PCIe.
                // Games reserve VRAM generously, so it gets more headroom before it warns.
                case Metric.Memory: return Above(value, 85, 95);
                case Metric.Vram: return Above(value, 90, 97);
                default: return Level.Normal;
            }
        }

        static Level Above(double value, double caution, double critical)
        {
            return value >= critical ? Level.Critical : value >= caution ? Level.Caution : Level.Normal;
        }
    }
}
