using System.Reflection;
using System.Reflection.Emit;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// A stand-in for an interface that leaves its default methods alone. Every abstract member of the
/// interface (and of the interfaces it inherits) is sent to <see cref="Handler"/> with the member's
/// name and arguments, so a test can answer it and see which calls a default method made. Members
/// that have a default body in the interface are not overridden, so calling them runs that body.
/// (#1268: the default bodies are production code that only a stand-in can reach.)
/// </summary>
public abstract class InterfaceStub
{
    public Func<string, object?[], object?> Handler { get; set; } = (name, _) => throw new NotSupportedException(name);

    public List<string> Calls { get; } = [];

    protected object? Dispatch(string name, object?[] args)
    {
        Calls.Add(name);
        return Handler(name, args);
    }

    private static readonly ModuleBuilder Module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("Deluno.Tests.InterfaceStubs"), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("stubs");

    private static readonly MethodInfo DispatchMethod = typeof(InterfaceStub)
        .GetMethod(nameof(Dispatch), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly Dictionary<Type, Type> Built = [];

    public static T Create<T>(Func<string, object?[], object?>? handler = null) where T : class
    {
        Type stubType;
        lock (Built)
        {
            if (!Built.TryGetValue(typeof(T), out stubType!))
            {
                stubType = Build(typeof(T));
                Built[typeof(T)] = stubType;
            }
        }

        var stub = (InterfaceStub)Activator.CreateInstance(stubType)!;
        if (handler is not null) stub.Handler = handler;
        return (T)(object)stub;
    }

    public static InterfaceStub Of(object stub) => (InterfaceStub)stub;

    private static Type Build(Type contract)
    {
        var type = Module.DefineType(
            "Stub_" + contract.Name + "_" + Guid.NewGuid().ToString("N"),
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(InterfaceStub),
            [contract]);
        type.DefineDefaultConstructor(MethodAttributes.Public);

        foreach (var iface in new[] { contract }.Concat(contract.GetInterfaces()))
        {
            type.AddInterfaceImplementation(iface);
            foreach (var method in iface.GetMethods().Where(m => m.IsAbstract))
            {
                if (method.IsGenericMethodDefinition) throw new NotSupportedException(method.Name);
                var parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
                var impl = type.DefineMethod(
                    iface.Name + "." + method.Name,
                    MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                    method.ReturnType,
                    parameters);
                var il = impl.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, method.Name);
                il.Emit(OpCodes.Ldc_I4, parameters.Length);
                il.Emit(OpCodes.Newarr, typeof(object));
                for (var i = 0; i < parameters.Length; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (parameters[i].IsValueType) il.Emit(OpCodes.Box, parameters[i]);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Call, DispatchMethod);
                if (method.ReturnType == typeof(void)) il.Emit(OpCodes.Pop);
                else if (method.ReturnType.IsValueType) il.Emit(OpCodes.Unbox_Any, method.ReturnType);
                else il.Emit(OpCodes.Castclass, method.ReturnType);
                il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(impl, method);
            }
        }

        return type.CreateType();
    }
}
