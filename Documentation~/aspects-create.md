# Create an aspect

To create an aspect, use the [`IAspect`](xref:Unity.Entities.IAspect) interface. You must declare an aspect as a readonly partial struct, and the struct must implement the `IAspect` interface:

```c#
using Unity.Entities;

readonly partial struct MyAspect : IAspect
{
    // Your Aspect code
}
```

## Fields

You can use `RefRW<T>` or `RefRO<T>` to declare a component as part of an aspect. To declare a buffer, use `DynamicBuffer<T>`. For more information on the fields available, see the [`IAspect`](xref:Unity.Entities.IAspect) documentation.

## Read-only and read-write access

Use the `RefRO` and `RefRW` fields to provide read-only, or read-write access to components in the aspect. When you want to reference an aspect in code, use `in` to override all references to become read-only, or `ref` to respect the read-only or read-write access declared in the aspect. 

If you use `in` to reference an aspect that has read-write access to components, it might throw exceptions on write attempts.

## Create aspect instances in a system

To create aspect instances in a system, call [`SystemAPI.GetAspectRW`](xref:Unity.Entities.SystemAPI.GetAspectRW*) or [`SystemAPI.GetAspectRO`](xref:Unity.Entities.SystemAPI.GetAspectRO*):

```c#
// Throws if the entity is missing any of 
// the required components of MyAspect.
MyAspect asp = SystemAPI.GetAspectRW<MyAspect>(myEntity);
```

If you use any method or property that attempts to modify the underlying components, then `SystemAPI.GetAspectRO` throws an error.

To create aspect instances outside of a system, use [`EntityManager.GetAspect`](xref:Unity.Entities.EntityManager.GetAspect*) or [`EntityManager.GetAspectRO`](xref:Unity.Entities.EntityManager.GetAspectRO*).

## Example

In this example, the `CannonBallAspect` sets the transform, position, and speed of the cannon ball Component in a tank themed game. 

```c#
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// Aspects must be declared as a readonly partial struct
readonly partial struct CannonBallAspect : IAspect<CannonBallAspect>
{
    // An Entity field in an Aspect gives access to the Entity itself.
    // This is required for registering commands in an EntityCommandBuffer for example.
    public readonly Entity Self;

    // Aspects can contain other aspects.
    readonly TransformAspect Transform;

    // A RefRW field provides read write access to a component. If the aspect is taken as an "in"
    // parameter, the field behaves as if it was a RefRO and throws exceptions on write attempts.
    readonly RefRW<CannonBall> CannonBall;

    // Properties like this aren't mandatory. The Transform field can be public instead.
    // But they improve readability by avoiding chains of "aspect.aspect.aspect.component.value.value".
    public float3 Position
    {
        get => Transform.Position;
        set => Transform.Position = value;
    }

    public float3 Speed
    {
        get => CannonBall.ValueRO.Speed;
        set => CannonBall.ValueRW.Speed = value;
    }
}
```

To use this aspect in other code, you can request `CannonBallAspect` in the same way as a component:

```c#

using Unity.Entities;
using Unity.Burst;

// It's best practice to Burst-compile your code
[BurstCompile]
partial struct CannonBallJob : IJobEntity
{
    void Execute(ref CannonBallAspect cannonBall)
    {
        // Your game logic
    }
}

```
