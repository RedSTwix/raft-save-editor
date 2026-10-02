using System.Formats.Nrbf;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: SaveStructureInspector <save.rgd> [items.metadata.txt]");
    return 2;
}

using FileStream stream = File.OpenRead(args[0]);
_ = NrbfDecoder.Decode(stream, out IReadOnlyDictionary<SerializationRecordId, SerializationRecord> map);
ClassRecord[] classes = map.Values.OfType<ClassRecord>().ToArray();
Dictionary<int, string> itemNames = args.Length == 2
    ? File.ReadLines(args[1])
        .Where(line => line.Length > 0 && line[0] != '#')
        .Select(line => line.Split('|'))
        .Where(fields => fields.Length >= 2 && int.TryParse(fields[0], out _))
        .ToDictionary(fields => int.Parse(fields[0]), fields => fields[1])
    : [];

Console.WriteLine($"Records={map.Count} Classes={classes.Length}");
ClassRecord[] blocks = classes
    .Where(record => record.TypeName.FullName.StartsWith("RGD_Block", StringComparison.Ordinal))
    .ToArray();
Console.WriteLine($"BLOCK SUMMARY Total={blocks.Length}");
if (blocks.Length > 0)
{
    float[] xs = blocks.Select(record => record.GetSingle("BlockPositionX")).ToArray();
    float[] ys = blocks.Select(record => record.GetSingle("BlockPositionY")).ToArray();
    float[] zs = blocks.Select(record => record.GetSingle("BlockPositionZ")).ToArray();
    Console.WriteLine($"BOUNDS X={xs.Min():0.##}..{xs.Max():0.##} Y={ys.Min():0.##}..{ys.Max():0.##} Z={zs.Min():0.##}..{zs.Max():0.##}");
    Console.WriteLine("LEVELS " + string.Join(" | ", blocks
        .GroupBy(record => MathF.Round(record.GetSingle("BlockPositionY") * 2) / 2)
        .OrderBy(group => group.Key)
        .Select(group => $"Y={group.Key:0.##}:{group.Count()}")));
    Console.WriteLine("BLOCK INDEX COUNTS");
    foreach (IGrouping<int, ClassRecord> group in blocks.GroupBy(record => record.GetInt32("BlockIndex")).OrderByDescending(group => group.Count()))
    {
        string name = itemNames.TryGetValue(group.Key, out string? value) ? value : "?";
        Console.WriteLine($"  {group.Key} | {name} | {group.Count()}");
    }

    ClassRecord[] foundations = blocks.Where(record => record.TypeName.FullName == "RGD_Block_Foundation").ToArray();
    int reinforced = foundations.Count(record => record.HasMember("IsReinforced") && record.GetBoolean("IsReinforced"));
    Console.WriteLine($"FOUNDATIONS Total={foundations.Length} Reinforced={reinforced}");
    PrintFoundationMap(foundations);
}
foreach (IGrouping<string, ClassRecord> group in classes
    .Where(record => record.TypeName.FullName.Contains("Block", StringComparison.OrdinalIgnoreCase)
        || record.TypeName.FullName.Contains("Raft", StringComparison.OrdinalIgnoreCase)
        || record.TypeName.FullName.Contains("Transform", StringComparison.OrdinalIgnoreCase))
    .GroupBy(record => record.TypeName.FullName)
    .OrderByDescending(group => group.Count()))
{
    Console.WriteLine($"TYPE {group.Key} COUNT {group.Count()}");
    Console.WriteLine($"  MEMBERS {string.Join(", ", group.First().MemberNames)}");
}

Console.WriteLine("SAMPLE BLOCK RECORDS");
foreach (ClassRecord record in classes
    .Where(record => record.TypeName.FullName.StartsWith("RGD_Block", StringComparison.Ordinal))
    .Take(12))
{
    Console.WriteLine($"{record.TypeName.FullName} #{record.Id}");
    foreach (string member in record.MemberNames)
    {
        object? value;
        try { value = record.GetRawValue(member); }
        catch (Exception ex) { value = $"<{ex.GetType().Name}>"; }
        Console.WriteLine($"  {member} = {Format(value)}");
    }
}

return 0;

static string Format(object? value) => value switch
{
    null => "null",
    ClassRecord record => $"{record.TypeName.FullName} #{record.Id} {{ {string.Join(", ", record.MemberNames.Select(name => name + "=" + SafeRaw(record, name)))} }}",
    ArrayRecord array => $"{array.TypeName.FullName} #{array.Id}",
    SerializationRecord record => $"{record.TypeName.FullName} #{record.Id}",
    _ => value.ToString() ?? "null"
};

static string SafeRaw(ClassRecord record, string member)
{
    try
    {
        object? value = record.GetRawValue(member);
        return value is SerializationRecord nested
            ? $"{nested.TypeName.FullName}#{nested.Id}"
            : value?.ToString() ?? "null";
    }
    catch { return "?"; }
}

static void PrintFoundationMap(ClassRecord[] foundations)
{
    if (foundations.Length == 0) return;
    var cells = foundations.Select(record => new
    {
        X = (int)MathF.Round(record.GetSingle("BlockPositionX") / 1.5f),
        Z = (int)MathF.Round(record.GetSingle("BlockPositionZ") / 1.5f),
        Reinforced = record.HasMember("IsReinforced") && record.GetBoolean("IsReinforced")
    }).ToArray();
    int minX = cells.Min(cell => cell.X), maxX = cells.Max(cell => cell.X);
    int minZ = cells.Min(cell => cell.Z), maxZ = cells.Max(cell => cell.Z);
    if (maxX - minX > 80 || maxZ - minZ > 80)
    {
        Console.WriteLine("FOUNDATION MAP omitted: dimensions exceed 80x80 cells.");
        return;
    }
    HashSet<(int X, int Z)> normal = cells.Where(cell => !cell.Reinforced).Select(cell => (cell.X, cell.Z)).ToHashSet();
    HashSet<(int X, int Z)> armored = cells.Where(cell => cell.Reinforced).Select(cell => (cell.X, cell.Z)).ToHashSet();
    Console.WriteLine($"FOUNDATION MAP {maxX - minX + 1}x{maxZ - minZ + 1} (# reinforced, + normal)");
    for (int z = maxZ; z >= minZ; z--)
    {
        char[] row = new char[maxX - minX + 1];
        Array.Fill(row, ' ');
        for (int x = minX; x <= maxX; x++)
            row[x - minX] = armored.Contains((x, z)) ? '#' : normal.Contains((x, z)) ? '+' : ' ';
        Console.WriteLine(new string(row).TrimEnd());
    }
}
