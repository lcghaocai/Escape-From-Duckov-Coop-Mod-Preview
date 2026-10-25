global using LiteNetLib;
global using LiteNetLib.Utils;
global using UnityEngine;
global using EscapeFromDuckovCoopMod;

namespace UnityEngine
{
    // The transport only uses Unity for logging; tests run the real UDP library.
    internal static class Debug
    {
        public static readonly List<string> Errors = new();
        public static readonly List<string> Warnings = new();
        public static void LogError(object value) => Errors.Add(value.ToString());
        public static void LogWarning(object value) => Warnings.Add(value.ToString());
    }
}


namespace EscapeFromDuckovCoopMod
{
    internal static class CoopLogSystem
    {
        public static void WriteNetworkDiagnostic(string message) => UnityEngine.Debug.Warnings.Add(message);
    }
}
