using System.Diagnostics;
using System.Text;

static async Task<int> Main(string[] args)
{
    var opts = Options.Parse(args);
    if (!opts.Valid) { Console.Error.WriteLine(opts.Error); return 2; }

    var stockfishPath = opts.StockfishPath ?? "stockfish-windows-x86-64-avx2";

    using var stockfish = new UciProcess(stockfishPath);
    using var machine   = new UciProcess(GetMachinePath());

    await stockfish.EnsureReady();
    await machine.EnsureReady();

    string posCmd = opts.StartPos ? "position startpos" : $"position fen {opts.Fen}";

    await stockfish.Send(posCmd);
    await machine.Send(posCmd);

    if (opts.Divide)
    {
        var sfDivide = await stockfish.GoDivide(opts.Depth, opts.Verbose);
        var meDivide = await machine.GoDivide(opts.Depth, opts.Verbose);
        var report = Comparator.CompareDivide(sfDivide, meDivide, opts);
        Console.WriteLine(report);
        if (report.Contains('❌') && opts.StopOnError) return 1;
    }
    else
    {
        long sf = await stockfish.GoPerft(opts.Depth);
        long me = await machine.GoPerft(opts.Depth);
        Console.WriteLine($"Position: {(opts.StartPos ? "startpos" : opts.Fen)}");
        Console.WriteLine($"Depth: {opts.Depth}");
        Console.WriteLine($"Stockfish: {sf} nodes");
        Console.WriteLine($"Machine:   {me} nodes {(sf==me?"✅":"❌ ("+(me-sf)+")")}");
        if (sf != me && opts.StopOnError) return 1;
    }

    return 0;
}

static string GetMachinePath()
{
    // Use dotnet to run the Machine project in-process for portability
    // You can adjust to direct exe if you publish Machine self-contained
    return "dotnet"; // arg handling is in UciProcess when program is dotnet
}

sealed class Options
{
    public bool StartPos { get; private set; }
    public string Fen { get; private set; } = string.Empty;
    public int Depth { get; private set; }
    public bool Divide { get; private set; }
    public bool Verbose { get; private set; }
    public bool StopOnError { get; private set; }
    public string? StockfishPath { get; private set; }

    public bool Valid { get; private set; }
    public string Error { get; private set; } = string.Empty;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--startpos": o.StartPos = true; break;
                case "--fen": o.Fen = i+1 < args.Length ? args[++i] : ""; break;
                case "--depth": o.Depth = i+1 < args.Length && int.TryParse(args[++i], out var d) ? d : 0; break;
                case "--divide": o.Divide = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--stop-on-error": o.StopOnError = true; break;
                case "--stockfish-path": o.StockfishPath = i+1 < args.Length ? args[++i] : null; break;
            }
        }
        o.Valid = (o.StartPos ^ !string.IsNullOrWhiteSpace(o.Fen)) && o.Depth > 0;
        if (!o.Valid) o.Error = "Usage: --startpos|--fen <FEN> --depth N [--divide] [--verbose] [--stop-on-error] [--stockfish-path <path>]";
        return o;
    }
}

sealed class UciProcess : IDisposable
{
    private readonly Process _proc;
    private readonly bool _isDotnet;
    private readonly StringBuilder _sb = new();

    public UciProcess(string path)
    {
        _isDotnet = string.Equals(path, "dotnet", StringComparison.OrdinalIgnoreCase);
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = _isDotnet ? "run --project Machine" : string.Empty,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_sb) _sb.AppendLine(e.Data); };
        _proc.Start();
        _proc.BeginOutputReadLine();
    }

    public async Task EnsureReady()
    {
        await Send("uci");
        await ReadUntil("uciok");
        await Send("isready");
        await ReadUntil("readyok");
    }

    public async Task Send(string cmd)
    {
        await _proc.StandardInput.WriteLineAsync(cmd);
        await _proc.StandardInput.FlushAsync();
    }

    public async Task<long> GoPerft(int depth)
    {
        if (_isDotnet)
        {
            await Send($"go perft {depth}");
            // Our engine prints: info string perft depth D nodes N time ...
            string? line = await ReadUntil("bestmove", includeLine:false, capture:"info string perft ");
            long nodes = ParseNodes(line ?? string.Empty);
            // We may emit bestmove 0000 afterward; ensure we consume it if present
            await Drain("bestmove");
            return nodes;
        }
        else
        {
            await Send($"go perft {depth}");
            // Stockfish prints final line "Nodes searched: N" or sum after list
            string? line = await ReadUntil("Nodes searched:");
            return ParseTailNumber(line ?? string.Empty);
        }
    }

    public async Task<Dictionary<string,long>> GoDivide(int depth, bool verbose)
    {
        // Use divide when available; for Stockfish use go perft depth and parse per-move lines
        await Send($"go perft {depth}");
        var map = new Dictionary<string,long>();
        while (true)
        {
            var l = await ReadLineAsync(timeoutMs: 50);
            if (l == null) break;
            if (l.StartsWith("bestmove")) break;
            if (l.Contains(':'))
            {
                var parts = l.Split(':', 2);
                var move = parts[0].Trim();
                var cnt = ParseTailNumber(parts[1]);
                map[move] = cnt;
                if (verbose) Console.WriteLine($"{move}: {cnt}");
            }
            else if (l.StartsWith("Nodes searched:"))
            {
                break;
            }
        }
        return map;
    }

    private static long ParseNodes(string s)
    {
        // parse ... nodes N ...
        var idx = s.IndexOf(" nodes ", StringComparison.Ordinal);
        if (idx < 0) return 0;
        var tail = s[(idx+7)..];
        var sp = tail.IndexOf(' ');
        var number = sp >= 0 ? tail[..sp] : tail;
        return long.TryParse(number, out var n) ? n : 0;
    }

    private static long ParseTailNumber(string s)
    {
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (char.IsDigit(s[i]))
            {
                int end = i + 1;
                int start = i;
                while (start >= 0 && char.IsDigit(s[start])) start--;
                start++;
                if (long.TryParse(s[start..end], out var n)) return n;
                break;
            }
        }
        return 0;
    }

    private async Task<string?> ReadUntil(string marker, bool includeLine = true, string? capture = null)
    {
        while (true)
        {
            var l = await ReadLineAsync();
            if (l == null) return null;
            if (capture != null && l.Contains(capture, StringComparison.Ordinal)) return l;
            if (l.Contains(marker, StringComparison.Ordinal)) return includeLine ? l : null;
        }
    }

    private async Task Drain(string marker)
    {
        var _ = await ReadUntil(marker);
    }

    private async Task<string?> ReadLineAsync(int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            string? line = null;
            lock (_sb)
            {
                var text = _sb.ToString();
                var idx = text.IndexOf('\n');
                if (idx >= 0)
                {
                    line = text[..idx].TrimEnd('\r');
                    _sb.Remove(0, idx + 1);
                }
            }
            if (line != null) return line;
            await Task.Delay(5);
        }
        return null;
    }

    public void Dispose()
    {
        try { _proc.StandardInput.WriteLine("quit"); } catch { }
        try { _proc.Kill(true); } catch { }
        _proc.Dispose();
    }
}

static class Comparator
{
    public static string CompareDivide(Dictionary<string,long> sf, Dictionary<string,long> me, Options opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Position: {(opts.StartPos ? "startpos" : opts.Fen)}");
        sb.AppendLine($"Depth: {opts.Depth}");
        long sfTotal = 0, meTotal = 0;
        foreach (var v in sf.Values) sfTotal += v;
        foreach (var v in me.Values) meTotal += v;
        sb.AppendLine($"Stockfish: {sfTotal} nodes");
        sb.AppendLine($"Machine:   {meTotal} nodes {(sfTotal==meTotal?"✅":"❌ ("+(meTotal-sfTotal)+")")}");

        // Find first divergence by move (sorted lexicographically for determinism)
        foreach (var move in sf.Keys.OrderBy(k => k))
        {
            sf.TryGetValue(move, out var sfn);
            me.TryGetValue(move, out var men);
            if (sfn != men)
            {
                sb.AppendLine($"\nFirst divergence at depth {opts.Depth - 1}:");
                sb.AppendLine($"Move {move}: Stockfish={sfn}, Machine={men} ({men - sfn:+#;-#;0})");
                break;
            }
        }
        if (opts.Verbose)
        {
            sb.AppendLine("\nAll moves:");
            foreach (var move in sf.Keys.OrderBy(k => k))
            {
                me.TryGetValue(move, out var men);
                sb.AppendLine($"{move}: SF={sf[move]}, ME={men}");
            }
        }
        return sb.ToString();
    }
}

