using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TapUp
{
    internal static class ChestGameContract
    {
        // Fingerprints of the chest code in the verified Windows depots.
        // The rest of Assembly-CSharp is outside this compatibility check.
        private static readonly string[] KnownFingerprints = {
            "30423EA4C5EDB531D21D30365635F63CAF0317524D3BC3462E96E0508B9C4942",
            "43EA0736BBD3D75FC72AA8F667D74C46DF62512F89CB61A69F2BE042EB9BD776",
            "ABCCA5E560E6E123178CA75D38F22CFFFDE4201A64A7A9B56BB88CC7C77A1B0D",
            "FA1D64A14CD088D3883276012BB038A980D97E9AA31F19CA5B957066A2F04518"
        };

        private static readonly Regex IteratorOrdinal = new Regex(@"(<[^<>]+>d__)\d+");

        internal static void Verify(ModuleDefinition module)
        {
            if (Array.IndexOf(KnownFingerprints, Fingerprint(module)) < 0)
                throw new InvalidOperationException("Изменился или отсутствует код BongoCat, от которого зависит автосбор. Подключение не установлено.");
        }

        internal static string Fingerprint(ModuleDefinition module)
        {
            var contract = new StringBuilder();
            var shop = RequireType(module, "BongoCat.Shop");
            var item = RequireType(module, "BongoCat.ShopItem");
            AddField(contract, shop, "ChestIsReady", "System.Boolean");
            AddField(contract, shop, "StockRefreshTimeLeft", "System.Int32");
            AddField(contract, shop, "_openingChest", "System.Boolean");
            AddField(contract, shop, "_isEmoteShop", "System.Boolean");
            AddField(contract, shop, "_shopItem", item.FullName);
            AddField(contract, item, "_waitingForServer", "System.Boolean");
            AddField(contract, item, "_shop", shop.FullName);
            AddProperty(contract, shop, "CanGetChest");
            AddProperty(contract, shop, "CanAffordChest");
            AddMethods(contract, shop, "Start", "OnChestReady", "TimerUpdate");
            AddGeneratedMethods(contract, shop, "<Start>");
            AddGeneratedMethods(contract, shop, "<TimerUpdate>");
            AddMethods(contract, item, "Buy", "StartBuy", "CheckAffordable", "CanBuy", "Callback", "OnEnable", "OnDisable");
            AddGeneratedMethods(contract, item, "<StartBuy>");
            AddMethods(contract, RequireType(module, "BongoCat.Pets"), "get_Current", "CanSpendPets", "TrySpendPets", "AddStatsListener", "RemoveStatsListener");
            var exchanger = RequireType(module, "Steam.ChestExchanger");
            AddMethods(contract, exchanger, "OpenChest", "ExchangeWhenReady");
            AddGeneratedMethods(contract, exchanger, "<ExchangeWhenReady>");
            var exchange = RequireType(module, "BongoCat.SteamJsonParser.SteamExchange");
            AddMethods(contract, exchange, "Exchange", "TryToFulfill");
            AddGeneratedMethods(contract, exchange, "<Exchange>");
            return GamePatch.Hash(Encoding.UTF8.GetBytes(contract.ToString()));
        }

        private static TypeDefinition RequireType(ModuleDefinition module, string name)
        {
            var type = module.GetType(name);
            if (type == null) throw MissingMember(name);
            return type;
        }

        private static InvalidOperationException MissingMember(string name)
        {
            return new InvalidOperationException("Изменился API автосбора BongoCat: " + name);
        }

        private static void AddField(StringBuilder contract, TypeDefinition type, string name, string fieldType)
        {
            var fields = type.Fields.Where(f => !f.IsStatic && f.Name == name).ToArray();
            if (fields.Length != 1 || fields[0].FieldType.FullName != fieldType) throw MissingMember(type.FullName + "::" + name);
            contract.Append(fields[0].FullName).Append('\n');
        }

        private static void AddProperty(StringBuilder contract, TypeDefinition type, string name)
        {
            var properties = type.Properties.Where(p => p.Name == name).ToArray();
            if (properties.Length != 1 || properties[0].Parameters.Count != 0 ||
                properties[0].PropertyType.FullName != "System.Boolean" || properties[0].GetMethod == null)
                throw MissingMember(type.FullName + "::" + name);
            AddMethod(contract, properties[0].GetMethod);
        }

        private static void AddMethods(StringBuilder contract, TypeDefinition type, params string[] names)
        {
            foreach (string name in names)
            {
                var methods = type.Methods.Where(m => m.Name == name).ToArray();
                if (methods.Length != 1) throw MissingMember(type.FullName + "::" + name);
                AddMethod(contract, methods[0]);
            }
        }

        private static void AddGeneratedMethods(StringBuilder contract, TypeDefinition type, string prefix)
        {
            var generated = type.NestedTypes.SelectMany(nested =>
                nested.Name.StartsWith(prefix + "d__", StringComparison.Ordinal)
                    ? nested.Methods.Where(m => m.IsConstructor || m.Name == "MoveNext" || m.Name == "System.IDisposable.Dispose")
                    : nested.Methods.Where(m => m.Name.StartsWith(prefix + "b__", StringComparison.Ordinal))).ToArray();
            foreach (var method in generated.OrderBy(m => Normalize(m.FullName), StringComparer.Ordinal))
                AddMethod(contract, method);
        }

        private static string Normalize(string text) { return IteratorOrdinal.Replace(text, "$1"); }

        private static void AddMethod(StringBuilder contract, MethodDefinition method)
        {
            if (!method.HasBody) throw MissingMember(method.FullName);
            var body = method.Body;
            contract.Append(Normalize(method.FullName)).Append('|').Append((int)method.Attributes).Append('|')
                .Append((int)method.ImplAttributes).Append('|').Append(body.InitLocals).Append('\n');
            foreach (var local in body.Variables)
                contract.Append("local ").Append(Operand(local.VariableType, body)).Append('\n');
            foreach (var instruction in body.Instructions)
            {
                string opcode = instruction.OpCode.Name;
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget) opcode = opcode.Substring(0, opcode.Length - 2);
                contract.Append(opcode).Append(' ').Append(Operand(instruction.Operand, body)).Append('\n');
            }
            foreach (var handler in body.ExceptionHandlers)
                contract.Append("handler ").Append(handler.HandlerType).Append('|')
                    .Append(Operand(handler.TryStart, body)).Append('|').Append(Operand(handler.TryEnd, body)).Append('|')
                    .Append(Operand(handler.HandlerStart, body)).Append('|').Append(Operand(handler.HandlerEnd, body)).Append('|')
                    .Append(Operand(handler.FilterStart, body)).Append('|').Append(Operand(handler.CatchType, body)).Append('\n');
        }

        private static string Operand(object value, MethodBody body)
        {
            var target = value as Instruction;
            if (target != null) return body.Instructions.IndexOf(target).ToString(CultureInfo.InvariantCulture);
            var targets = value as Instruction[];
            if (targets != null) return String.Join(",", targets.Select(i => Operand(i, body)));
            var type = value as TypeReference;
            if (type != null) return Normalize(type.FullName) + "@" + type.Scope.Name;
            var member = value as MemberReference;
            if (member != null) return Normalize(member.FullName) + "@" + member.DeclaringType.Scope.Name;
            var parameter = value as ParameterDefinition;
            if (parameter != null) return parameter.Index.ToString(CultureInfo.InvariantCulture);
            var variable = value as VariableDefinition;
            if (variable != null) return variable.Index.ToString(CultureInfo.InvariantCulture);
            if (value is string) return Convert.ToBase64String(Encoding.UTF8.GetBytes((string)value));
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }
}
