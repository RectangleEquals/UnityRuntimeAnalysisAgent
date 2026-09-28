using System;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;

namespace UnityRuntimeAnalysisAgent.Core.Transport;

/// <summary>
/// Creates a pipe server instance restricted to the current user, through reflection (netstandard2.0 has no pipe ACL API).
/// Supports the .NET Framework/Mono constructor that takes a <c>PipeSecurity</c>, and .NET's <c>NamedPipeServerStreamAcl.Create</c>.
/// </summary>
internal static class PipeSecurityBinder
{
    private const int InOut = (int)PipeDirection.InOut;

    public static bool TryCreate(string pipeName, int maxInstances, out NamedPipeServerStream? stream, out string? failure)
    {
        stream = null;
        failure = null;
        try
        {
            var security = CreateCurrentUserSecurity(out failure);
            if (security is null)
            {
                return false;
            }

            // .NET Framework / Mono: NamedPipeServerStream(string, PipeDirection, int, PipeTransmissionMode, PipeOptions, int, int, PipeSecurity)
            var ctor = typeof(NamedPipeServerStream).GetConstructors().FirstOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 8 && p[7].ParameterType == security.GetType();
            });
            if (ctor is not null)
            {
                stream = (NamedPipeServerStream)ctor.Invoke(new object[]
                {
                    pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security,
                });
                return true;
            }

            // .NET: NamedPipeServerStreamAcl.Create(string, PipeDirection, int, PipeTransmissionMode, PipeOptions, int, int, PipeSecurity?, HandleInheritability, PipeAccessRights)
            var acl = FindType("System.IO.Pipes.NamedPipeServerStreamAcl", "System.IO.Pipes.AccessControl", "System.IO.Pipes");
            var create = acl?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 10);
            if (create is not null)
            {
                var parameters = create.GetParameters();
                var inheritability = Enum.ToObject(parameters[8].ParameterType, 0);
                var extraRights = Enum.ToObject(parameters[9].ParameterType, 0);
                stream = (NamedPipeServerStream)create.Invoke(null, new object[]
                {
                    pipeName, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security, inheritability, extraRights,
                });
                return true;
            }

            failure = "no pipe constructor accepting a PipeSecurity was found";
            return false;
        }
        catch (TargetInvocationException e)
        {
            failure = (e.InnerException ?? e).Message;
            return false;
        }
        catch (Exception e)
        {
            failure = e.Message;
            return false;
        }
    }

    private static object? CreateCurrentUserSecurity(out string? failure)
    {
        failure = null;
        var securityType = FindType("System.IO.Pipes.PipeSecurity", "System.IO.Pipes.AccessControl", "System.Core", "System.IO.Pipes");
        var ruleType = FindType("System.IO.Pipes.PipeAccessRule", "System.IO.Pipes.AccessControl", "System.Core", "System.IO.Pipes");
        var rightsType = FindType("System.IO.Pipes.PipeAccessRights", "System.IO.Pipes.AccessControl", "System.Core", "System.IO.Pipes");
        var controlType = FindType("System.Security.AccessControl.AccessControlType", "System.Security.AccessControl", "mscorlib", "System.Private.CoreLib");
        var identityType = FindType("System.Security.Principal.WindowsIdentity", "System.Security.Principal.Windows", "mscorlib");
        var referenceType = FindType("System.Security.Principal.IdentityReference", "System.Security.Principal.Windows", "System.Security.Principal", "mscorlib");
        if (securityType is null || ruleType is null || rightsType is null || controlType is null || identityType is null || referenceType is null)
        {
            failure = "pipe access control types aren't available in this runtime";
            return null;
        }

        var current = identityType.GetMethod("GetCurrent", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
        var user = current?.GetType().GetProperty("User")?.GetValue(current);
        if (user is null)
        {
            failure = "the current user's SID isn't available";
            return null;
        }

        var fullControl = Enum.Parse(rightsType, "FullControl");
        var allow = Enum.Parse(controlType, "Allow");
        var ruleCtor = ruleType.GetConstructor(new[] { referenceType, rightsType, controlType });
        if (ruleCtor is null)
        {
            failure = "PipeAccessRule(IdentityReference, PipeAccessRights, AccessControlType) isn't available";
            return null;
        }

        var rule = ruleCtor.Invoke(new[] { user, fullControl, allow });
        var security = Activator.CreateInstance(securityType);
        securityType.GetMethod("AddAccessRule", new[] { ruleType })!.Invoke(security, new[] { rule });
        return security;
    }

    private static Type? FindType(string fullName, params string[] assemblies)
    {
        var type = Type.GetType(fullName, throwOnError: false);
        if (type is not null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, throwOnError: false);
            if (type is not null)
            {
                return type;
            }
        }

        foreach (var name in assemblies)
        {
            try
            {
                type = Assembly.Load(new AssemblyName(name)).GetType(fullName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // Not present in this runtime.
            }
        }

        return null;
    }
}
