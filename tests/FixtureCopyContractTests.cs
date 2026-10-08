// Contract for the actual legacy suite's private FakeTransport. No engine or I/O is started.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace EdgeGlow
{
    internal static class FixtureCopyContractTests
    {
        private static int passed, failed, assertions;
        private static readonly Type fixtureType = typeof(RealComponentRegressionTests).GetNestedType("FakeTransport", BindingFlags.NonPublic);
        private static readonly Type clockType = typeof(RealComponentRegressionTests).GetNestedType("Clock", BindingFlags.NonPublic);
        public static int Main(string[] args)
        {
            Run("D03 all eight fields and input isolation", AllFieldsAndInputIsolation);
            Run("D03 returned target and endpoint snapshot isolation", ReturnedSnapshotIsolation);
            Run("D03 nullable false zero and absent target preservation", NullableStates);
            Run("D03 rebind invalidation and observed-generation rediscovery", GenerationTransition);
            Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed; " + assertions + " assertions.");
            Console.WriteLine("Actual legacy FakeTransport via reflection; no engine, native capture, sockets, GUI, settings or registry.");
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static IEngineTransport NewTransport()
        {
            Check(fixtureType != null && clockType != null, "actual legacy nested fixture types missing");
            object clock = Activator.CreateInstance(clockType, true);
            return (IEngineTransport)Activator.CreateInstance(fixtureType, new object[] { clock });
        }
        private static DeviceInfo Sentinel()
        {
            return new DeviceInfo { Id = "COPY-SENTINEL-37", Ip = "192.168.50.137", Sku = "H6609-SENTINEL",
                LastSeenUtc = new DateTime(2026, 3, 7, 11, 13, 17, DateTimeKind.Utc),
                LastStatusUtc = new DateTime(2025, 9, 2, 5, 19, 23, DateTimeKind.Utc),
                PowerOn = true, Brightness = 43, IdentityGeneration = 37 };
        }
        private static void MutateAll(DeviceInfo value)
        {
            value.Id = "MUTATED"; value.Ip = "192.168.50.211"; value.Sku = "MUTATED-SKU";
            value.LastSeenUtc = DateTime.MinValue; value.LastStatusUtc = DateTime.MaxValue;
            value.PowerOn = false; value.Brightness = 91; value.IdentityGeneration = -7;
        }
        private static void Snapshot(DeviceInfo actual, DeviceInfo expected, string label)
        {
            Check(actual != null, label + " missing snapshot");
            List<string> differences = new List<string>();
            Field(differences, actual.Id == expected.Id, "Id");
            Field(differences, actual.Ip == expected.Ip, "Ip");
            Field(differences, actual.Sku == expected.Sku, "Sku");
            Field(differences, actual.LastSeenUtc == expected.LastSeenUtc && actual.LastSeenUtc.Kind == expected.LastSeenUtc.Kind, "LastSeenUtc");
            Field(differences, actual.LastStatusUtc == expected.LastStatusUtc && actual.LastStatusUtc.Kind == expected.LastStatusUtc.Kind, "LastStatusUtc");
            Field(differences, actual.PowerOn == expected.PowerOn, "PowerOn");
            Field(differences, actual.Brightness == expected.Brightness, "Brightness");
            Field(differences, actual.IdentityGeneration == expected.IdentityGeneration, "IdentityGeneration");
            Check(differences.Count == 0, label + " omitted or altered: " + String.Join(", ", differences.ToArray()));
        }
        private static void Field(List<string> errors, bool matches, string name)
        { assertions++; if (!matches) errors.Add(name); }
        private static void AllFieldsAndInputIsolation()
        {
            using (IEngineTransport transport = NewTransport()) {
                DeviceInfo input = Sentinel(), expected = Sentinel();
                transport.SetTarget(input);
                Snapshot(transport.CurrentTarget, expected, "SetTarget to CurrentTarget");
                EndpointToken token = transport.CurrentEndpoint;
                Snapshot(token.Snapshot, expected, "SetTarget to CurrentEndpoint");
                Check(token.Generation == transport.Generation, "token did not pin transport generation");
                MutateAll(input);
                Snapshot(transport.CurrentTarget, expected, "mutated caller input");
                Snapshot(token.Snapshot, expected, "token after caller mutation");
            }
        }
        private static void ReturnedSnapshotIsolation()
        {
            using (IEngineTransport transport = NewTransport()) {
                DeviceInfo expected = Sentinel(); transport.SetTarget(expected);
                DeviceInfo returned = transport.CurrentTarget; MutateAll(returned);
                Snapshot(transport.CurrentTarget, expected, "mutated returned CurrentTarget");
                EndpointToken token = transport.CurrentEndpoint;
                DeviceInfo tokenSnapshot = token.Snapshot; MutateAll(tokenSnapshot);
                Snapshot(token.Snapshot, expected, "mutated returned token Snapshot");
                Snapshot(transport.CurrentEndpoint.Snapshot, expected, "mutated token cannot change fixture");
                DeviceInfo replacement = Sentinel(); replacement.Id = "REPLACEMENT"; replacement.IdentityGeneration = 42;
                transport.SetTarget(replacement);
                Snapshot(token.Snapshot, expected, "old token after target replacement");
                Snapshot(transport.CurrentTarget, replacement, "replacement target");
            }
        }
        private static void NullableStates()
        {
            using (IEngineTransport transport = NewTransport()) {
                DeviceInfo expected = Sentinel(); expected.PowerOn = false; expected.Brightness = 0;
                transport.SetTarget(expected); Snapshot(transport.CurrentTarget, expected, "false and zero");
                expected = Sentinel(); expected.PowerOn = null; expected.Brightness = null;
                transport.SetTarget(expected); Snapshot(transport.CurrentTarget, expected, "unknown nullable status");
                Snapshot(transport.CurrentEndpoint.Snapshot, expected, "unknown token status");
                transport.SetTarget(null);
                Check(transport.CurrentTarget == null && transport.CurrentEndpoint == null, "null target invented a snapshot");
            }
        }
        private static void GenerationTransition()
        {
            using (IEngineTransport transport = NewTransport()) {
                DeviceInfo original = Sentinel(); original.IdentityGeneration = transport.Generation;
                transport.SetTarget(original); EndpointToken oldToken = transport.CurrentEndpoint;
                int oldGeneration = transport.Generation;
                fixtureType.GetMethod("Rebind").Invoke(transport, null);
                Check(transport.Generation == oldGeneration + 1, "rebind must advance transport generation once");
                DeviceInfo invalid = transport.CurrentTarget;
                Check(invalid.IdentityGeneration == -1 && invalid.LastSeenUtc == DateTime.MinValue &&
                    invalid.LastStatusUtc == DateTime.MinValue && invalid.PowerOn == null && invalid.Brightness == null,
                    "rebind retained a valid observation/status from the retired transport");
                Check(invalid.Id == original.Id && invalid.Ip == original.Ip && invalid.Sku == original.Sku,
                    "rebind discarded retained identity/address rather than only observation validity");
                Check(oldToken.Generation == oldGeneration, "old token transport generation mutated");
                Snapshot(oldToken.Snapshot, original, "old token after rebind");
                DeviceInfo observed = null;
                transport.DeviceSeen += delegate(DeviceInfo reply) { observed = reply; transport.SetTarget(reply); };
                transport.Discover();
                Check(observed != null && observed.IdentityGeneration == transport.Generation, "rediscovery did not carry current observed generation");
                Check(observed.LastSeenUtc != DateTime.MinValue && observed.PowerOn == null && observed.Brightness == null &&
                    observed.LastStatusUtc == DateTime.MinValue, "scan invented power state or omitted freshness");
                EndpointToken fresh = transport.CurrentEndpoint;
                Check(fresh.Generation == transport.Generation, "new token uses retired transport generation");
                Snapshot(fresh.Snapshot, observed, "rediscovered current endpoint");
                MutateAll(observed);
                Check(fresh.Snapshot.IdentityGeneration == transport.Generation && transport.CurrentTarget.IdentityGeneration == transport.Generation,
                    "mutating rediscovery result altered retained observation");
            }
        }
    }
}
