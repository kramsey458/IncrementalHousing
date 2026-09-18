using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;

// Inspects the actual compiled adapter against the installed game's blacklist.
// This does not instantiate Unity objects or claim to be a live gameplay test.
static class AdapterApiChecks
{
    public static void Verify(string modPath, string managed)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            var path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
        };
        var game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(managed, "Timberborn.BaseComponentSystem.dll")));
        var blacklistType = game.GetType("Timberborn.BaseComponentSystem.TypeBlacklist", true)!;
        var blacklist = Activator.CreateInstance(blacklistType, true)!;
        var verify = blacklistType.GetMethod("Verify")!;
        // Reproduce the reported exception using the real game's managed API.
        bool rejected = false;
        try { verify.Invoke(blacklist, new object[] { game.GetType("Timberborn.BaseComponentSystem.BaseComponent", true)! }); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Game's component blacklist contract changed.");

        var mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(modPath));
        var service = mod.GetType("IncrementalHousing.HousingService", true)!;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(op => unchecked((ushort)op.Value));
        bool populationUsesAllComponents = false;
        int checkedRetrievals = 0;
        foreach (var method in service.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) continue;
            for (int pos = 0; pos < il.Length;)
            {
                ushort code = il[pos++];
                if (code == 0xfe) code = (ushort)(0xfe00 | il[pos++]);
                var op = opcodes[code];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, pos), service.GetGenericArguments(), method.GetGenericArguments())!;
                    if (called.DeclaringType?.FullName == "Timberborn.BaseComponentSystem.BaseComponent")
                    {
                        if (method.Name == "GetPopulation" && called.Name == "get_AllComponents") populationUsesAllComponents = true;
                        if (called.IsGenericMethod && called.Name.Contains("Component"))
                            foreach (var type in called.GetGenericArguments())
                                if (!type.ContainsGenericParameters) { verify.Invoke(blacklist, new object[] { type }); checkedRetrievals++; }
                    }
                }
                pos += op.OperandType switch {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, pos),
                    _ => 4
                };
            }
        }
        if (!populationUsesAllComponents || checkedRetrievals == 0)
            throw new Exception("Adapter must use AllComponents for breeding detection and pass the game's retrieval blacklist.");
        Console.WriteLine($"Adapter API check: crash reproduced against real blacklist; {checkedRetrievals} concrete retrieval calls accepted; GetPopulation uses AllComponents.");
    }
}
