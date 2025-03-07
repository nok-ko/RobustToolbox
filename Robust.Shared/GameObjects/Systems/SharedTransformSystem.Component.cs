using JetBrains.Annotations;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Utility;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Robust.Shared.Map.Components;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Robust.Shared.Containers;

namespace Robust.Shared.GameObjects;

public abstract partial class SharedTransformSystem
{
    #region Anchoring

    internal void ReAnchor(
        EntityUid uid,
        TransformComponent xform,
        MapGridComponent oldGrid,
        MapGridComponent newGrid,
        Vector2i tilePos,
        EntityUid oldGridUid,
        EntityUid newGridUid,
        TransformComponent oldGridXform,
        TransformComponent newGridXform,
        EntityQuery<TransformComponent> xformQuery)
    {
        // Bypass some of the expensive stuff in unanchoring / anchoring.
        _map.RemoveFromSnapGridCell(oldGridUid, oldGrid, tilePos, uid);
        _map.AddToSnapGridCell(newGridUid, newGrid, tilePos, uid);
        // TODO: Could do this re-parent way better.
        // Unfortunately we don't want any anchoring events to go out hence... this.
        xform._anchored = false;
        oldGridXform._children.Remove(uid);
        newGridXform._children.Add(uid);
        xform._parent = newGridUid;
        xform._anchored = true;

        SetGridId(uid, xform, newGridUid, xformQuery);
        var reParent = new EntParentChangedMessage(uid, oldGridUid, xform.MapID, xform);
        RaiseLocalEvent(uid, ref reParent, true);
        // TODO: Ideally shouldn't need to call the moveevent
        var movEevee = new MoveEvent(uid,
            new EntityCoordinates(oldGridUid, xform._localPosition),
            new EntityCoordinates(newGridUid, xform._localPosition),
            xform.LocalRotation,
            xform.LocalRotation,
            xform,
            _gameTiming.ApplyingState);
        RaiseLocalEvent(uid, ref movEevee, true);

        DebugTools.Assert(xformQuery.GetComponent(oldGridUid).MapID == xformQuery.GetComponent(newGridUid).MapID);
        DebugTools.Assert(xform._anchored);

        Dirty(uid, xform);
        var ev = new ReAnchorEvent(uid, oldGridUid, newGridUid, tilePos, xform);
        RaiseLocalEvent(uid, ref ev);
    }

    [Obsolete("Use Entity<T> variant")]
    public bool AnchorEntity(
        EntityUid uid,
        TransformComponent xform,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i tileIndices)
    {
        return AnchorEntity((uid, xform), (gridUid, grid), tileIndices);
    }

    public bool AnchorEntity(
        Entity<TransformComponent> entity,
        Entity<MapGridComponent> grid,
        Vector2i tileIndices)
    {
        var (uid, xform) = entity;
        if (!_map.AddToSnapGridCell(grid, grid, tileIndices, uid))
            return false;

        var wasAnchored = entity.Comp._anchored;
        xform._anchored = true;
        var meta = MetaData(uid);
        Dirty(entity, meta);

        // Mark as static before doing position changes, to avoid the velocity change on parent change.
        _physics.TrySetBodyType(uid, BodyType.Static, xform: xform);

        if (!wasAnchored && xform.Running)
        {
            var ev = new AnchorStateChangedEvent(xform);
            RaiseLocalEvent(uid, ref ev, true);
        }

        // Anchor snapping. If there is a coordinate change, it will dirty the component for us.
        var pos = new EntityCoordinates(grid, _map.GridTileToLocal(grid, grid, tileIndices).Position);
        SetCoordinates((uid, xform, meta), pos, unanchor: false);
        return true;
    }

    [Obsolete("Use Entity<T> variants")]
    public bool AnchorEntity(EntityUid uid, TransformComponent xform, MapGridComponent grid)
    {
        var tileIndices = _map.TileIndicesFor(grid.Owner, grid, xform.Coordinates);
        return AnchorEntity(uid, xform, grid.Owner, grid, tileIndices);
    }

    public bool AnchorEntity(EntityUid uid, TransformComponent xform)
    {
        return AnchorEntity((uid, xform));
    }

    public bool AnchorEntity(Entity<TransformComponent> entity, Entity<MapGridComponent>? grid = null)
    {
        DebugTools.Assert(grid == null || grid.Value.Owner == entity.Comp.GridUid);

        if (grid == null)
        {
            if (!TryComp(entity.Comp.GridUid, out MapGridComponent? gridComp))
                return false;
            grid = (entity.Comp.GridUid.Value, gridComp);
        }

        var tileIndices =  _map.TileIndicesFor(grid.Value, grid.Value, entity.Comp.Coordinates);
        return AnchorEntity(entity, grid.Value, tileIndices);
    }

    public void Unanchor(EntityUid uid, TransformComponent xform, bool setPhysics = true)
    {
        if (!xform._anchored)
            return;

        Dirty(uid, xform);
        xform._anchored = false;

        if (setPhysics)
            _physics.TrySetBodyType(uid, BodyType.Dynamic, xform: xform);

        if (xform.LifeStage < ComponentLifeStage.Initialized)
            return;

        if (_gridQuery.TryGetComponent(xform.GridUid, out var grid))
        {
            var tileIndices = _map.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates);
            _map.RemoveFromSnapGridCell(xform.GridUid.Value, grid, tileIndices, uid);
        }

        if (!xform.Running)
            return;

        var ev = new AnchorStateChangedEvent(xform);
        RaiseLocalEvent(uid, ref ev, true);
    }

    #endregion

    #region Contains

    /// <summary>
    ///     Checks whether the first entity or one of it's children is the parent of some other entity.
    /// </summary>
    public bool ContainsEntity(EntityUid parent, Entity<TransformComponent?> child)
    {
        if (!Resolve(child.Owner, ref child.Comp))
            return false;

        if (!child.Comp.ParentUid.IsValid())
            return false;

        if (parent == child.Comp.ParentUid)
            return true;

        if (!XformQuery.TryGetComponent(child.Comp.ParentUid, out var parentXform))
            return false;

        return ContainsEntity(parent, (child.Comp.ParentUid, parentXform));
    }

    #endregion

    #region Component Lifetime

    private void OnCompInit(EntityUid uid, TransformComponent component, ComponentInit args)
    {
        // Children MAY be initialized here before their parents are.
        // We do this whole dance to handle this recursively,
        // setting _mapIdInitialized along the way to avoid going to the MapComponent every iteration.
        static MapId FindMapIdAndSet(EntityUid uid, TransformComponent xform, IEntityManager entMan, EntityQuery<TransformComponent> xformQuery, IMapManager mapManager)
        {
            if (xform._mapIdInitialized)
                return xform.MapID;

            MapId value;

            if (xform.ParentUid.IsValid())
            {
                value = FindMapIdAndSet(xform.ParentUid, xformQuery.GetComponent(xform.ParentUid), entMan, xformQuery, mapManager);
            }
            else
            {
                // second level node, terminates recursion up the branch of the tree
                if (entMan.TryGetComponent(uid, out MapComponent? mapComp))
                {
                    value = mapComp.MapId;
                }
                else
                {
                    // We allow entities to be spawned directly into null-space.
                    value = MapId.Nullspace;
                }
            }

            xform.MapUid = value == MapId.Nullspace ? null : mapManager.GetMapEntityId(value);
            xform.MapID = value;
            xform._mapIdInitialized = true;
            return value;
        }

        if (!component._mapIdInitialized)
        {
            FindMapIdAndSet(uid, component, EntityManager, XformQuery, _mapManager);
            component._mapIdInitialized = true;
        }

        // Has to be done if _parent is set from ExposeData.
        if (component.ParentUid.IsValid())
        {
            // Note that _children is a HashSet<EntityUid>,
            // so duplicate additions (which will happen) don't matter.

            var parentXform = XformQuery.GetComponent(component.ParentUid);
            if (parentXform.LifeStage > ComponentLifeStage.Running || LifeStage(component.ParentUid) > EntityLifeStage.MapInitialized)
            {
                var msg = $"Attempted to re-parent to a terminating object. Entity: {ToPrettyString(component.ParentUid)}, new parent: {ToPrettyString(uid)}";
#if EXCEPTION_TOLERANCE
                Log.Error(msg);
                Del(uid);
#else
                throw new InvalidOperationException(msg);
#endif
            }

            parentXform._children.Add(uid);
        }

        InitializeGridUid(uid, component);
        component.MatricesDirty = true;

        DebugTools.Assert(component._gridUid == uid || !HasComp<MapGridComponent>(uid));
        if (!component._anchored)
            return;

        MapGridComponent? grid;

        // First try find grid via parent:
        if (component.GridUid == component.ParentUid && TryComp(component.ParentUid, out MapGridComponent? gridComp))
        {
            grid = gridComp;
        }
        else
        {
            // Entity may not be directly parented to the grid (e.g., spawned using some relative entity coordinates)
            // in that case, we attempt to attach to a grid.
            var pos = new MapCoordinates(GetWorldPosition(component), component.MapID);
            _mapManager.TryFindGridAt(pos, out _, out grid);
        }

        if (grid == null)
        {
            Unanchor(uid, component);
            return;
        }

        if (!AnchorEntity(uid, component, grid))
            component._anchored = false;
    }

    internal void InitializeGridUid(
        EntityUid uid,
        TransformComponent xform)
    {
        // Dont set pre-init, as the map grid component might not have been added yet.
        if (xform._gridInitialized || xform.LifeStage < ComponentLifeStage.Initializing)
            return;

        xform._gridInitialized = true;
        DebugTools.Assert(xform.GridUid == null);
        if (_gridQuery.HasComponent(uid))
        {
            xform._gridUid = uid;
            return;
        }

        if (!xform._parent.IsValid())
            return;

        var parentXform = XformQuery.GetComponent(xform._parent);
        InitializeGridUid(xform._parent, parentXform);
        xform._gridUid = parentXform._gridUid;
    }

    private void OnCompStartup(EntityUid uid, TransformComponent xform, ComponentStartup args)
    {
        // TODO PERFORMANCE remove AnchorStateChangedEvent and EntParentChangedMessage events here.

        // I hate this. Apparently some entities rely on this to perform their initialization logic (e.g., power
        // receivers or lights?). Those components should just do their own init logic, instead of wasting time raising
        // this event on every entity that gets created.
        if (xform.Anchored)
        {
            DebugTools.Assert(xform.ParentUid == xform.GridUid && xform.ParentUid.IsValid());
            var anchorEv = new AnchorStateChangedEvent(xform);
            RaiseLocalEvent(uid, ref anchorEv, true);
        }

        // I hate this too. Once again, required for shit like containers because they CBF doing their own init logic
        // and rely on parent changed messages instead. Might also be used by broadphase stuff?
        var parentEv = new EntParentChangedMessage(uid, null, MapId.Nullspace, xform);
        RaiseLocalEvent(uid, ref parentEv, true);

        var ev = new TransformStartupEvent(xform);
        RaiseLocalEvent(uid, ref ev, true);

        DebugTools.Assert(!xform.NoLocalRotation || xform.LocalRotation == 0, $"NoRot entity has a non-zero local rotation. entity: {ToPrettyString(uid)}");
    }

    #endregion

    #region GridId

    /// <summary>
    /// Sets the <see cref="GridId"/> for the transformcomponent without updating its children. Does not Dirty it.
    /// </summary>
    internal void SetGridIdNoRecursive(EntityUid uid, TransformComponent xform, EntityUid? gridUid)
    {
        DebugTools.Assert(gridUid == uid || !HasComp<MapGridComponent>(uid));
        if (xform._gridUid == gridUid)
            return;

        DebugTools.Assert(gridUid == null || HasComp<MapGridComponent>(gridUid));
        xform._gridUid = gridUid;
    }

    /// <summary>
    /// Sets the <see cref="GridId"/> for the transformcomponent. Does not Dirty it.
    /// </summary>
    public void SetGridId(EntityUid uid, TransformComponent xform, EntityUid? gridId, EntityQuery<TransformComponent>? xformQuery = null)
    {
        if (!xform._gridInitialized || xform._gridUid == gridId || xform.GridUid == uid)
            return;

        DebugTools.Assert(!HasComp<MapGridComponent>(uid) || gridId == uid);
        xform._gridUid = gridId;
        var childEnumerator = xform.ChildEnumerator;

        while (childEnumerator.MoveNext(out var child))
        {
            SetGridId(child.Value, XformQuery.GetComponent(child.Value), gridId);
        }
    }

    #endregion

    #region Local Position

    [Obsolete("use override with EntityUid")]
    public void SetLocalPosition(TransformComponent xform, Vector2 value)
    {
        SetLocalPosition(xform.Owner, value, xform);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void SetLocalPosition(EntityUid uid, Vector2 value, TransformComponent? xform = null)
        => SetLocalPositionNoLerp(uid, value, xform);


    [Obsolete("use override with EntityUid")]
    public void SetLocalPositionNoLerp(TransformComponent xform, Vector2 value)
        => SetLocalPositionNoLerp(xform.Owner, value, xform);

    public void SetLocalPositionNoLerp(EntityUid uid, Vector2 value, TransformComponent? xform = null)
    {
        if (!XformQuery.Resolve(uid, ref xform))
            return;

#pragma warning disable CS0618
        xform.LocalPosition = value;
#pragma warning restore CS0618
    }

    #endregion

    #region Local Rotation

    public void SetLocalRotationNoLerp(EntityUid uid, Angle value, TransformComponent? xform = null)
    {
        if (!XformQuery.Resolve(uid, ref xform))
            return;

        xform.LocalRotation = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void SetLocalRotation(EntityUid uid, Angle value, TransformComponent? xform = null)
        => SetLocalRotationNoLerp(uid, value, xform);

    [Obsolete("use override with EntityUid")]
    public void SetLocalRotation(TransformComponent xform, Angle value)
    {
        SetLocalRotation(xform.Owner, value, xform);
    }

    #endregion

    #region Coordinates

    public void SetCoordinates(EntityUid uid, EntityCoordinates value)
    {
        SetCoordinates((uid, Transform(uid), MetaData(uid)), value);
    }

    /// <summary>
    ///     This sets the local position and parent of an entity.
    /// </summary>
    /// <param name="rotation">Final local rotation. If not specified, this will attempt to preserve world
    /// rotation.</param>
    /// <param name="unanchor">Whether or not to unanchor the entity before moving. Note that this will still move the
    /// entity even when false. If you set this to false, you need to manually manage the grid lookup changes and ensure
    /// the final position is valid</param>
    public void SetCoordinates(
        Entity<TransformComponent, MetaDataComponent> entity,
        EntityCoordinates value,
        Angle? rotation = null,
        bool unanchor = true,
        TransformComponent? newParent = null,
        TransformComponent? oldParent = null)
    {
        var (uid, xform, meta) = entity;
        // NOTE: This setter must be callable from before initialize.

        if (xform.ParentUid == value.EntityId
            && xform._localPosition.EqualsApprox(value.Position)
            && (rotation == null || MathHelper.CloseTo(rotation.Value.Theta, xform._localRotation.Theta)))
        {
            return;
        }

        var oldPosition = xform._parent.IsValid() ? new EntityCoordinates(xform._parent, xform._localPosition) : default;
        var oldRotation = xform._localRotation;

        if (xform.Anchored && unanchor)
            Unanchor(uid, xform);

        if (value.EntityId != xform.ParentUid && value.EntityId.IsValid())
        {
            if (meta.EntityLifeStage >= EntityLifeStage.Terminating)
            {
                Log.Error($"{ToPrettyString(uid)} is attempting to move while terminating. New parent: {ToPrettyString(value.EntityId)}. Trace: {Environment.StackTrace}");
                return;
            }

            if (TerminatingOrDeleted(value.EntityId))
            {
                Log.Error($"{ToPrettyString(uid)} is attempting to attach itself to a terminating entity {ToPrettyString(value.EntityId)}. Trace: {Environment.StackTrace}");
                return;
            }
        }

        // Set new values
        Dirty(uid, xform, meta);
        xform.MatricesDirty = true;
        xform._localPosition = value.Position;

        if (rotation != null && !xform.NoLocalRotation)
            xform._localRotation = rotation.Value;

        DebugTools.Assert(!xform.NoLocalRotation || xform.LocalRotation == 0);

        // Perform parent change logic
        if (value.EntityId != xform._parent)
        {
            if (value.EntityId == uid)
            {
                DetachParentToNull(uid, xform);
                if (_netMan.IsServer || IsClientSide(uid))
                    QueueDel(uid);
                throw new InvalidOperationException($"Attempted to parent an entity to itself: {ToPrettyString(uid)}");
            }

            if (value.EntityId.IsValid())
            {
                if (!XformQuery.Resolve(value.EntityId, ref newParent, false))
                {
                    DetachParentToNull(uid, xform);
                    if (_netMan.IsServer || IsClientSide(uid))
                        QueueDel(uid);
                    throw new InvalidOperationException($"Attempted to parent entity {ToPrettyString(uid)} to non-existent entity {value.EntityId}");
                }

                if (newParent.LifeStage >= ComponentLifeStage.Stopping || LifeStage(value.EntityId) >= EntityLifeStage.Terminating)
                {
                    DetachParentToNull(uid, xform);
                    if (_netMan.IsServer || IsClientSide(uid))
                        QueueDel(uid);
                    throw new InvalidOperationException($"Attempted to re-parent to a terminating object. Entity: {ToPrettyString(uid)}, new parent: {ToPrettyString(value.EntityId)}");
                }

                // Check for recursive/circular transform hierarchies.
                if (xform.MapUid == newParent.MapUid)
                {
                    var recursiveUid = value.EntityId;
                    var recursiveXform = newParent;
                    while (recursiveXform.ParentUid.IsValid())
                    {
                        if (recursiveXform.ParentUid == uid)
                        {
                            if (!_gameTiming.ApplyingState)
                                throw new InvalidOperationException($"Attempted to parent an entity to one of its descendants! {ToPrettyString(uid)}. new parent: {ToPrettyString(value.EntityId)}");

                            // Client is halfway through applying server state, which can sometimes lead to a temporarily circular transform hierarchy.
                            // E.g., client is holding a foldable bed and predicts dropping & sitting in it -> reset to holding it -> bed is parent of player and vice versa.
                            // Even though its temporary, this can still cause the client to get stuck in infinite loops while applying the game state.
                            // So we will just break the loop by detaching to null and just trusting that the loop wasn't actually a real feature of the server state.
                            Log.Warning($"Encountered circular transform hierarchy while applying state for entity: {ToPrettyString(uid)}. Detaching child to null: {ToPrettyString(recursiveUid)}");
                            DetachParentToNull(recursiveUid, recursiveXform);
                            break;
                        }

                        recursiveUid = recursiveXform.ParentUid;
                        recursiveXform = XformQuery.GetComponent(recursiveUid);
                    }
                }
            }

            if (xform._parent.IsValid())
                XformQuery.Resolve(xform._parent, ref oldParent);

            oldParent?._children.Remove(uid);
            newParent?._children.Add(uid);

            xform._parent = value.EntityId;
            var oldMapId = xform.MapID;

            if (newParent != null)
            {
                xform.ChangeMapId(newParent.MapID, XformQuery);

                if (!xform._gridInitialized)
                    InitializeGridUid(uid, xform);
                else
                {
                    if (!newParent._gridInitialized)
                        InitializeGridUid(value.EntityId, newParent);
                    SetGridId(uid, xform, newParent.GridUid);
                }
            }
            else
            {
                xform.ChangeMapId(MapId.Nullspace, XformQuery);
                if (!xform._gridInitialized)
                    InitializeGridUid(uid, xform);
                else
                    SetGridId(uid, xform, null, XformQuery);
            }

            if (xform.Initialized)
            {
                // preserve world rotation
                if (rotation == null && oldParent != null && newParent != null && !xform.NoLocalRotation)
                    xform._localRotation += GetWorldRotation(oldParent) - GetWorldRotation(newParent);

                DebugTools.Assert(!xform.NoLocalRotation || xform.LocalRotation == 0);

                var entParentChangedMessage = new EntParentChangedMessage(uid, oldParent?.Owner, oldMapId, xform);
                RaiseLocalEvent(uid, ref entParentChangedMessage, true);
            }
        }

        if (!xform.Initialized)
            return;

        var newPosition = xform._parent.IsValid() ? new EntityCoordinates(xform._parent, xform._localPosition) : default;
#if DEBUG
        // If an entity is parented to the map, its grid uid should be null (unless it is itself a grid or we have a map-grid)
        if (xform.ParentUid == xform.MapUid)
            DebugTools.Assert(xform.GridUid == null || xform.GridUid == uid || xform.GridUid == xform.MapUid);
#endif
        var moveEvent = new MoveEvent(uid, oldPosition, newPosition, oldRotation, xform._localRotation, xform, _gameTiming.ApplyingState);
        RaiseLocalEvent(uid, ref moveEvent, true);
    }

    public void SetCoordinates(
        EntityUid uid,
        TransformComponent xform,
        EntityCoordinates value,
        Angle? rotation = null,
        bool unanchor = true,
        TransformComponent? newParent = null,
        TransformComponent? oldParent = null)
    {
        SetCoordinates((uid, xform, MetaData(uid)), value, rotation, unanchor, newParent, oldParent);
    }

    #endregion

    #region Parent

    public void ReparentChildren(EntityUid oldUid, EntityUid uid)
    {
        ReparentChildren(oldUid, uid, XformQuery);
    }

    /// <summary>
    /// Re-parents all of the oldUid's children to the new entity.
    /// </summary>
    public void ReparentChildren(EntityUid oldUid, EntityUid uid, EntityQuery<TransformComponent> xformQuery)
    {
        if (oldUid == uid)
        {
            Log.Error($"Tried to reparent entities from the same entity, {ToPrettyString(oldUid)}");
            return;
        }

        var oldXform = xformQuery.GetComponent(oldUid);
        var xform = xformQuery.GetComponent(uid);

        foreach (var child in oldXform._children.ToArray())
        {
            SetParent(child, xformQuery.GetComponent(child), uid, xformQuery, xform);
        }

        DebugTools.Assert(oldXform.ChildCount == 0);
    }

    public TransformComponent? GetParent(EntityUid uid)
    {
        return GetParent(XformQuery.GetComponent(uid));
    }

    public TransformComponent? GetParent(TransformComponent xform)
    {
        if (!xform.ParentUid.IsValid())
            return null;
        return XformQuery.GetComponent(xform.ParentUid);
    }

    public EntityUid GetParentUid(EntityUid uid)
    {
        return XformQuery.GetComponent(uid).ParentUid;
    }

    public void SetParent(EntityUid uid, EntityUid parent)
    {
        SetParent(uid, XformQuery.GetComponent(uid), parent, XformQuery);
    }

    public void SetParent(EntityUid uid, TransformComponent xform, EntityUid parent, TransformComponent? parentXform = null)
    {
        SetParent(uid, xform, parent, XformQuery, parentXform);
    }

    public void SetParent(EntityUid uid, TransformComponent xform, EntityUid parent, EntityQuery<TransformComponent> xformQuery, TransformComponent? parentXform = null)
    {
        DebugTools.Assert(uid == xform.Owner);
        if (xform.ParentUid == parent)
            return;

        if (!parent.IsValid())
        {
            DetachParentToNull(uid, xform);
            return;
        }

        if (!xformQuery.Resolve(parent, ref parentXform))
            return;

        var (_, parRot, parInvMatrix) = GetWorldPositionRotationInvMatrix(parentXform, xformQuery);
        var (pos, rot) = GetWorldPositionRotation(xform, xformQuery);
        var newPos = parInvMatrix.Transform(pos);
        var newRot = rot - parRot;

        SetCoordinates(uid, xform, new EntityCoordinates(parent, newPos), newRot, newParent: parentXform);
    }

    #endregion

    #region States
    public virtual void ActivateLerp(EntityUid uid, TransformComponent xform) { }

    internal void OnGetState(EntityUid uid, TransformComponent component, ref ComponentGetState args)
    {
        DebugTools.Assert(!component.ParentUid.IsValid() || (!Deleted(component.ParentUid) && !EntityManager.IsQueuedForDeletion(component.ParentUid)));
        var parent = GetNetEntity(component.ParentUid);

        args.State = new TransformComponentState(
            component.LocalPosition,
            component.LocalRotation,
            parent,
            component.NoLocalRotation,
            component.Anchored);
    }

    internal void OnHandleState(EntityUid uid, TransformComponent xform, ref ComponentHandleState args)
    {
        if (args.Current is TransformComponentState newState)
        {
            var parent = GetEntity(newState.ParentID);
            if (!parent.IsValid() && newState.ParentID.IsValid())
                Log.Error($"Received transform component state with an unknown parent Id. Entity: {ToPrettyString(uid)}. Net parent: {newState.ParentID}");

            var oldAnchored = xform.Anchored;

            // update actual position data, if required
            if (!xform.LocalPosition.EqualsApprox(newState.LocalPosition)
                || !xform.LocalRotation.EqualsApprox(newState.Rotation)
                || xform.ParentUid != parent)
            {
                // remove from any old grid lookups
                if (xform.Anchored && TryComp(xform.ParentUid, out MapGridComponent? grid))
                {
                    var tileIndices = _map.TileIndicesFor(xform.ParentUid, grid, xform.Coordinates);
                    _map.RemoveFromSnapGridCell(xform.ParentUid, grid, tileIndices, uid);
                }

                // Set anchor state true during the move event unless the entity wasn't and isn't being anchored. This avoids unnecessary entity lookup changes.
                xform._anchored |= newState.Anchored;

                // Update the action position, rotation, and parent (and hence also map, grid, etc).
                SetCoordinates(uid, xform, new EntityCoordinates(parent, newState.LocalPosition), newState.Rotation, unanchor: false);

                xform._anchored = newState.Anchored;

                // Add to any new grid lookups. Normal entity lookups will either have been handled by the move event,
                // or by the following AnchorStateChangedEvent
                if (xform._anchored && xform.Initialized)
                {
                    if (xform.ParentUid == xform.GridUid && TryComp(xform.GridUid, out MapGridComponent? newGrid))
                    {
                        var tileIndices = _map.TileIndicesFor(xform.GridUid.Value, newGrid, xform.Coordinates);
                        _map.AddToSnapGridCell(xform.GridUid.Value, newGrid, tileIndices, uid);
                    }
                    else
                    {
                        DebugTools.Assert("New transform state coordinates are incompatible with anchoring.");
                        xform._anchored = false;
                    }
                }
            }
            else
            {
                xform.Anchored = newState.Anchored;
            }

            if (oldAnchored != newState.Anchored && xform.Initialized)
            {
                var ev = new AnchorStateChangedEvent(xform);
                RaiseLocalEvent(uid, ref ev, true);
            }

            xform._noLocalRotation = newState.NoLocalRotation;

            DebugTools.Assert(xform.ParentUid == parent, "Transform state failed to set parent");
            DebugTools.Assert(xform.Anchored == newState.Anchored, "Transform state failed to set anchored");
        }

        if (args.Next is TransformComponentState nextTransform
            && nextTransform.ParentID == GetNetEntity(xform.ParentUid))
        {
            xform.NextPosition = nextTransform.LocalPosition;
            xform.NextRotation = nextTransform.Rotation;
            ActivateLerp(uid, xform);
        }
    }

    #endregion

    #region World Matrix

    [Pure]
    public Matrix3 GetWorldMatrix(EntityUid uid)
    {
        return GetWorldMatrix(XformQuery.GetComponent(uid), XformQuery);
    }

    // Temporary until it's moved here
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetWorldMatrix(TransformComponent component)
    {
        return GetWorldMatrix(component, XformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetWorldMatrix(EntityUid uid, EntityQuery<TransformComponent> xformQuery)
    {
        return GetWorldMatrix(xformQuery.GetComponent(uid), xformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetWorldMatrix(TransformComponent component, EntityQuery<TransformComponent> xformQuery)
    {
        var (pos, rot) = GetWorldPositionRotation(component, xformQuery);
        return Matrix3.CreateTransform(pos, rot);
    }

    #endregion

    #region World Position

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 GetWorldPosition(EntityUid uid)
    {
        return GetWorldPosition(XformQuery.GetComponent(uid));
    }

    // Temporary until it's moved here
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 GetWorldPosition(TransformComponent component)
    {
        Vector2 pos = component._localPosition;

        while (component.ParentUid != component.MapUid && component.ParentUid.IsValid())
        {
            component = XformQuery.GetComponent(component.ParentUid);
            pos = component._localRotation.RotateVec(pos) + component._localPosition;
        }

        return pos;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 GetWorldPosition(EntityUid uid, EntityQuery<TransformComponent> xformQuery)
    {
        return GetWorldPosition(xformQuery.GetComponent(uid));
    }

    [Pure]
    public Vector2 GetWorldPosition(TransformComponent component, EntityQuery<TransformComponent> xformQuery)
    {
        return GetWorldPosition(component);
    }

    [Pure]
    public MapCoordinates GetMapCoordinates(EntityUid entity, TransformComponent? xform = null)
    {
        if (!XformQuery.Resolve(entity, ref xform))
            return MapCoordinates.Nullspace;

        return GetMapCoordinates(xform);
    }

    [Pure]
    public MapCoordinates GetMapCoordinates(TransformComponent xform)
    {
        return new MapCoordinates(GetWorldPosition(xform), xform.MapID);
    }

    [Pure]
    public MapCoordinates GetMapCoordinates(Entity<TransformComponent> entity)
    {
        return GetMapCoordinates(entity.Comp);
    }

    [Pure]
    public (Vector2 WorldPosition, Angle WorldRotation) GetWorldPositionRotation(EntityUid uid)
    {
        return GetWorldPositionRotation(XformQuery.GetComponent(uid));
    }

    [Pure]
    public (Vector2 WorldPosition, Angle WorldRotation) GetWorldPositionRotation(TransformComponent component)
    {
        Vector2 pos = component._localPosition;
        Angle angle = component._localRotation;

        while (component.ParentUid != component.MapUid && component.ParentUid.IsValid())
        {
            component = XformQuery.GetComponent(component.ParentUid);
            pos = component._localRotation.RotateVec(pos) + component._localPosition;
            angle += component._localRotation;
        }

        return (pos, angle);
    }

    [Pure]
    public (Vector2 WorldPosition, Angle WorldRotation) GetWorldPositionRotation(TransformComponent component, EntityQuery<TransformComponent> xformQuery)
    {
        return GetWorldPositionRotation(component);
    }

    /// <summary>
    ///     Returns the position and rotation relative to some entity higher up in the component's transform hierarchy.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 Position, Angle Rotation) GetRelativePositionRotation(
        TransformComponent component,
        EntityUid relative,
        EntityQuery<TransformComponent> query)
    {
        var rot = component._localRotation;
        var pos = component._localPosition;
        var xform = component;
        while (xform.ParentUid != relative)
        {
            if (xform.ParentUid.IsValid() && query.TryGetComponent(xform.ParentUid, out xform))
            {
                rot += xform._localRotation;
                pos = xform._localRotation.RotateVec(pos) + xform._localPosition;
                continue;
            }

            // Entity was not actually in the transform hierarchy. This is probably a sign that something is wrong, or that the function is being misused.
            Log.Warning($"Target entity ({ToPrettyString(relative)}) not in transform hierarchy while calling {nameof(GetRelativePositionRotation)}.");
            var relXform = query.GetComponent(relative);
            pos = relXform.InvWorldMatrix.Transform(pos);
            rot = rot - GetWorldRotation(relXform, query);
            break;
        }

        return (pos, rot);
    }

    /// <summary>
    ///     Returns the position and rotation relative to some entity higher up in the component's transform hierarchy.
    /// </summary>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector2 GetRelativePosition(
        TransformComponent component,
        EntityUid relative,
        EntityQuery<TransformComponent> query)
    {
        var pos = component._localPosition;
        var xform = component;
        while (xform.ParentUid != relative)
        {
            if (xform.ParentUid.IsValid() && query.TryGetComponent(xform.ParentUid, out xform))
            {
                pos = xform._localRotation.RotateVec(pos) + xform._localPosition;
                continue;
            }

            // Entity was not actually in the transform hierarchy. This is probably a sign that something is wrong, or that the function is being misused.
            Log.Warning($"Target entity ({ToPrettyString(relative)}) not in transform hierarchy while calling {nameof(GetRelativePositionRotation)}.");
            var relXform = query.GetComponent(relative);
            pos = relXform.InvWorldMatrix.Transform(pos);
            break;
        }

        return pos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(EntityUid uid, Vector2 worldPos)
    {
        var xform = Transform(uid);
        SetWorldPosition(xform, worldPos);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(EntityUid uid, Vector2 worldPos, EntityQuery<TransformComponent> xformQuery)
    {
        var component = xformQuery.GetComponent(uid);
        SetWorldPosition(component, worldPos, xformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(TransformComponent component, Vector2 worldPos)
    {
        SetWorldPosition(component, worldPos, XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPosition(TransformComponent component, Vector2 worldPos, EntityQuery<TransformComponent> xformQuery)
    {
        if (!component._parent.IsValid())
        {
            DebugTools.Assert("Parent is invalid while attempting to set WorldPosition - did you try to move root node?");
            return;
        }

        var (curWorldPos, curWorldRot) = GetWorldPositionRotation(component, xformQuery);
        var negativeParentWorldRot = component._localRotation - curWorldRot;
        var newLocalPos = component._localPosition + negativeParentWorldRot.RotateVec(worldPos - curWorldPos);
        SetLocalPosition(component, newLocalPos);
    }

    #endregion

    #region World Rotation

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Angle GetWorldRotation(EntityUid uid)
    {
        return GetWorldRotation(XformQuery.GetComponent(uid), XformQuery);
    }

    // Temporary until it's moved here
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Angle GetWorldRotation(TransformComponent component)
    {
        return GetWorldRotation(component, XformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Angle GetWorldRotation(EntityUid uid, EntityQuery<TransformComponent> xformQuery)
    {
        return GetWorldRotation(xformQuery.GetComponent(uid), xformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Angle GetWorldRotation(TransformComponent component, EntityQuery<TransformComponent> xformQuery)
    {
        Angle rotation = component._localRotation;

        while (component.ParentUid != component.MapUid && component.ParentUid.IsValid())
        {
            component = xformQuery.GetComponent(component.ParentUid);
            rotation += component._localRotation;
        }

        return rotation;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotation(EntityUid uid, Angle angle)
    {
        var component = Transform(uid);
        SetWorldRotation(component, angle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotation(TransformComponent component, Angle angle)
    {
        var current = GetWorldRotation(component);
        var diff = angle - current;
        SetLocalRotation(component, component.LocalRotation + diff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotation(EntityUid uid, Angle angle, EntityQuery<TransformComponent> xformQuery)
    {
        SetWorldRotation(xformQuery.GetComponent(uid), angle, xformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldRotation(TransformComponent component, Angle angle, EntityQuery<TransformComponent> xformQuery)
    {
        var current = GetWorldRotation(component, xformQuery);
        var diff = angle - current;
        SetLocalRotation(component, component.LocalRotation + diff);
    }

    #endregion

    #region Set Position+Rotation

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [Obsolete("Use override with EntityUid")]
    public void SetWorldPositionRotation(TransformComponent component, Vector2 worldPos, Angle worldRot)
    {
        SetWorldPositionRotation(component.Owner, worldPos, worldRot, component);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetWorldPositionRotation(EntityUid uid, Vector2 worldPos, Angle worldRot, TransformComponent? component = null)
    {
        if (!XformQuery.Resolve(uid, ref component))
            return;

        if (!component._parent.IsValid())
        {
            DebugTools.Assert("Parent is invalid while attempting to set WorldPosition - did you try to move root node?");
            return;
        }

        var (curWorldPos, curWorldRot) = GetWorldPositionRotation(component);

        var negativeParentWorldRot = component.LocalRotation - curWorldRot;

        var newLocalPos = component.LocalPosition + negativeParentWorldRot.RotateVec(worldPos - curWorldPos);
        var newLocalRot = component.LocalRotation + worldRot - curWorldRot;

        SetLocalPositionRotation(uid, newLocalPos, newLocalRot, component);
    }

    [Obsolete("Use override with EntityUid")]
    public void SetLocalPositionRotation(TransformComponent xform, Vector2 pos, Angle rot)
        => SetLocalPositionRotation(xform.Owner, pos, rot, xform);

    /// <summary>
    ///     Simultaneously set the position and rotation. This is better than setting individually, as it reduces the number of move events and matrix rebuilding operations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual void SetLocalPositionRotation(EntityUid uid, Vector2 pos, Angle rot, TransformComponent? xform = null)
    {
        if (!XformQuery.Resolve(uid, ref xform))
            return;

        if (!xform._parent.IsValid())
        {
            DebugTools.Assert("Parent is invalid while attempting to set WorldPosition - did you try to move root node?");
            return;
        }

        if (xform._localPosition.EqualsApprox(pos) && xform.LocalRotation.EqualsApprox(rot))
            return;

        var oldPosition = xform.Coordinates;
        var oldRotation = xform.LocalRotation;

        if (!xform.Anchored)
            xform._localPosition = pos;

        if (!xform.NoLocalRotation)
            xform._localRotation = rot;

        DebugTools.Assert(!xform.NoLocalRotation || xform.LocalRotation == 0);

        Dirty(uid, xform);
        xform.MatricesDirty = true;

        if (!xform.Initialized)
            return;

        var moveEvent = new MoveEvent(uid, oldPosition, xform.Coordinates, oldRotation, rot, xform, _gameTiming.ApplyingState);
        RaiseLocalEvent(uid, ref moveEvent, true);
    }

    #endregion

    #region Inverse World Matrix

    [Pure]
    public Matrix3 GetInvWorldMatrix(EntityUid uid)
    {
        return GetInvWorldMatrix(XformQuery.GetComponent(uid), XformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetInvWorldMatrix(TransformComponent component)
    {
        return GetInvWorldMatrix(component, XformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetInvWorldMatrix(EntityUid uid, EntityQuery<TransformComponent> xformQuery)
    {
        return GetInvWorldMatrix(xformQuery.GetComponent(uid), xformQuery);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Matrix3 GetInvWorldMatrix(TransformComponent component, EntityQuery<TransformComponent> xformQuery)
    {
        var (pos, rot) = GetWorldPositionRotation(component, xformQuery);
        return Matrix3.CreateInverseTransform(pos, rot);
    }

    #endregion

    #region GetWorldPositionRotationMatrix
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix)
        GetWorldPositionRotationMatrix(EntityUid uid)
    {
        return GetWorldPositionRotationMatrix(XformQuery.GetComponent(uid), XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix)
        GetWorldPositionRotationMatrix(TransformComponent xform)
    {
        return GetWorldPositionRotationMatrix(xform, XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix)
        GetWorldPositionRotationMatrix(EntityUid uid, EntityQuery<TransformComponent> xforms)
    {
        return GetWorldPositionRotationMatrix(xforms.GetComponent(uid), xforms);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix)
        GetWorldPositionRotationMatrix(TransformComponent component, EntityQuery<TransformComponent> xforms)
    {
        var (pos, rot) = GetWorldPositionRotation(component, xforms);
        return (pos, rot, Matrix3.CreateTransform(pos, rot));
    }
    #endregion

    #region GetWorldPositionRotationInvMatrix

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 InvWorldMatrix) GetWorldPositionRotationInvMatrix(EntityUid uid)
    {
        return GetWorldPositionRotationInvMatrix(XformQuery.GetComponent(uid));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 InvWorldMatrix) GetWorldPositionRotationInvMatrix(TransformComponent xform)
    {
        return GetWorldPositionRotationInvMatrix(xform, XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 InvWorldMatrix) GetWorldPositionRotationInvMatrix(EntityUid uid, EntityQuery<TransformComponent> xforms)
    {
        return GetWorldPositionRotationInvMatrix(xforms.GetComponent(uid), xforms);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 InvWorldMatrix) GetWorldPositionRotationInvMatrix(TransformComponent component, EntityQuery<TransformComponent> xforms)
    {
        var (pos, rot) = GetWorldPositionRotation(component, xforms);
        return (pos, rot, Matrix3.CreateInverseTransform(pos, rot));
    }

    #endregion

    #region GetWorldPositionRotationMatrixWithInv

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix, Matrix3 InvWorldMatrix)
        GetWorldPositionRotationMatrixWithInv(EntityUid uid)
    {
        return GetWorldPositionRotationMatrixWithInv(XformQuery.GetComponent(uid), XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix, Matrix3 InvWorldMatrix)
        GetWorldPositionRotationMatrixWithInv(TransformComponent xform)
    {
        return GetWorldPositionRotationMatrixWithInv(xform, XformQuery);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix, Matrix3 InvWorldMatrix)
        GetWorldPositionRotationMatrixWithInv(EntityUid uid, EntityQuery<TransformComponent> xforms)
    {
        return GetWorldPositionRotationMatrixWithInv(xforms.GetComponent(uid), xforms);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector2 WorldPosition, Angle WorldRotation, Matrix3 WorldMatrix, Matrix3 InvWorldMatrix)
        GetWorldPositionRotationMatrixWithInv(TransformComponent component, EntityQuery<TransformComponent> xforms)
    {
        var (pos, rot) = GetWorldPositionRotation(component, xforms);
        return (pos, rot, Matrix3.CreateTransform(pos, rot), Matrix3.CreateInverseTransform(pos, rot));
    }

    #endregion

    #region AttachToGridOrMap
    /// <summary>
    /// Attempts to re-parent the given entity to the grid or map that the entity is on.
    /// If no valid map or grid is found, this will detach the entity to null-space and queue it for deletion.
    /// </summary>
    public void AttachToGridOrMap(EntityUid uid, TransformComponent? xform = null)
    {
        if (TerminatingOrDeleted(uid))
            return;

        if (!XformQuery.Resolve(uid, ref xform))
            return;

        if (!xform.ParentUid.IsValid() || xform.ParentUid == xform.GridUid)
            return;

        EntityUid newParent;
        var oldPos = GetWorldPosition(xform);
        if (_mapManager.TryFindGridAt(xform.MapID, oldPos, out var gridUid, out _)
            && !TerminatingOrDeleted(gridUid))
        {
            newParent = gridUid;
        }
        else if (_mapManager.GetMapEntityId(xform.MapID) is { Valid: true } mapEnt
            && !TerminatingOrDeleted(mapEnt))
        {
            newParent = mapEnt;
        }
        else
        {
            if (!_mapManager.IsMap(uid))
                Log.Warning($"Failed to attach entity to map or grid. Entity: ({ToPrettyString(uid)}). Trace: {Environment.StackTrace}");

            DetachParentToNull(uid, xform);
            return;
        }

        if (newParent == xform.ParentUid || newParent == uid)
            return;

        var newPos = GetInvWorldMatrix(newParent).Transform(oldPos);
        SetCoordinates(uid, xform, new(newParent, newPos));
    }

    public bool TryGetMapOrGridCoordinates(EntityUid uid, [NotNullWhen(true)] out EntityCoordinates? coordinates, TransformComponent? xform = null)
    {
        coordinates = null;

        if (!XformQuery.Resolve(uid, ref xform))
            return false;

        if (!xform.ParentUid.IsValid())
            return false;

        EntityUid newParent;
        var oldPos = GetWorldPosition(xform, XformQuery);
        if (_mapManager.TryFindGridAt(xform.MapID, oldPos, XformQuery, out var gridUid, out _))
        {
            newParent = gridUid;
        }
        else if (_mapManager.GetMapEntityId(xform.MapID) is { Valid: true } mapEnt)
        {
            newParent = mapEnt;
        }
        else
        {
            return false;
        }

        coordinates = new(newParent, GetInvWorldMatrix(newParent, XformQuery).Transform(oldPos));
        return true;
    }
    #endregion

    #region State Handling

    public void DetachParentToNull(EntityUid uid, TransformComponent xform)
    {
        XformQuery.TryGetComponent(xform.ParentUid, out var oldXform);
        DetachParentToNull(uid, xform, oldXform);
    }

    public void DetachParentToNull(EntityUid uid, TransformComponent xform, TransformComponent? oldXform)
    {
        DetachParentToNull((uid, xform, MetaData(uid)), oldXform);
    }

    public void DetachParentToNull(Entity<TransformComponent,MetaDataComponent> entity, TransformComponent? oldXform, bool terminating = false)
    {
        var (uid, xform, meta) = entity;

        if (!terminating && meta.EntityLifeStage >= EntityLifeStage.Terminating)
        {
            // Something is attempting to remove the entity from this entity's parent while it is in the process of being deleted.
            Log.Error($"Attempting to detach a terminating entity: {ToPrettyString(uid, meta)}. Trace: {Environment.StackTrace}");
            return;
        }

        var parent = xform._parent;
        if (!parent.IsValid())
        {
            DebugTools.Assert(!xform.Anchored,
                $"Entity is anchored but has no parent? Entity: {ToPrettyString(uid)}");

            DebugTools.Assert((MetaData(uid).Flags & MetaDataFlags.InContainer) == 0x0,
                $"Entity is in a container but has no parent? Entity: {ToPrettyString(uid)}");

            if (xform.Broadphase != null)
            {
                DebugTools.Assert(
                    xform.Broadphase == BroadphaseData.Invalid
                    || xform.Broadphase.Value.Uid == uid
                    || Deleted(xform.Broadphase.Value.Uid)
                    || Terminating(xform.Broadphase.Value.Uid),
                $"Entity has no parent but is on some broadphase? Entity: {ToPrettyString(uid)}. Broadphase: {ToPrettyString(xform.Broadphase.Value.Uid)}");
            }
            return;
        }

        // Before making any changes to physics or transforms, remove from the current broadphase
        _lookup.RemoveFromEntityTree(uid, xform);

        // Stop any active lerps
        xform.NextPosition = null;
        xform.NextRotation = null;
        xform.LerpParent = EntityUid.Invalid;

        if (xform.Anchored
            && _metaQuery.TryGetComponent(xform.GridUid, out var gridMeta)
            && gridMeta.EntityLifeStage <= EntityLifeStage.MapInitialized)
        {
            var grid = Comp<MapGridComponent>(xform.GridUid.Value);
            var tileIndices = _map.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates);
            _map.RemoveFromSnapGridCell(xform.GridUid.Value, grid, tileIndices, uid);
            xform._anchored = false;
            var anchorStateChangedEvent = new AnchorStateChangedEvent(xform, true);
            RaiseLocalEvent(uid, ref anchorStateChangedEvent, true);
        }

        SetCoordinates(entity, default, Angle.Zero, oldParent: oldXform);

        DebugTools.Assert((meta.Flags & MetaDataFlags.InContainer) == 0x0,
            $"Entity is in a container after having been detached to null-space? Entity: {ToPrettyString(uid)}");
    }

    #endregion

    private void OnGridAdd(EntityUid uid, TransformComponent component, GridAddEvent args)
    {
        // Added to existing map so need to update all children too.
        if (LifeStage(uid) > EntityLifeStage.Initialized)
        {
            SetGridId(uid, component, uid, XformQuery);
            return;
        }

        component._gridInitialized = true;
        component._gridUid = uid;
    }

    /// <summary>
    /// Attempts to drop an entity onto the map or grid next to another entity. If the target entity is in a container,
    /// this will attempt to insert that entity into the same container. Otherwise it will attach the entity to the
    /// grid or map at the same world-position as the target entity.
    /// </summary>
    public void DropNextTo(Entity<TransformComponent?> entity, Entity<TransformComponent?> target)
    {
        var xform = entity.Comp;
        if (!XformQuery.Resolve(entity, ref xform))
            return;

        var targetXform = target.Comp;
        if (!XformQuery.Resolve(target, ref targetXform) || !targetXform.ParentUid.IsValid())
        {
            DetachParentToNull(entity, xform);
            return;
        }

        var coords = targetXform.Coordinates;

        // recursively check for containers.
        var targetUid = target.Owner;
        while (targetXform.ParentUid.IsValid())
        {
            if (_container.IsEntityInContainer(targetUid)
                && _container.TryGetContainingContainer(targetXform.ParentUid, targetUid, out var container,
                    skipExistCheck: true)
                && _container.Insert((entity, xform, null, null), container))
            {
                return;
            }

            targetUid = targetXform.ParentUid;
            targetXform = XformQuery.GetComponent(targetUid);
        }

        SetCoordinates(entity, xform, coords);
        AttachToGridOrMap(entity, xform);
    }

    /// <summary>
    /// Attempts to place one entity next to another entity. If the target entity is in a container, this will attempt
    /// to insert that entity into the same container. Otherwise it will attach the entity to the same parent.
    /// </summary>
    public void PlaceNextTo(Entity<TransformComponent?> entity, Entity<TransformComponent?> target)
    {
        var xform = entity.Comp;
        if (!XformQuery.Resolve(entity, ref xform))
            return;

        var targetXform = target.Comp;
        if (!XformQuery.Resolve(target, ref targetXform) || !targetXform.ParentUid.IsValid())
        {
            DetachParentToNull(entity, xform);
            return;
        }

        if (!_container.IsEntityInContainer(target))
        {
            SetCoordinates(entity, xform, targetXform.Coordinates);
            return;
        }

        var containerComp = Comp<ContainerManagerComponent>(targetXform.ParentUid);
        foreach (var container in containerComp.Containers.Values)
        {
            if (!container.Contains(target))
                continue;

            if (!_container.Insert((entity, xform, null, null), container))
                PlaceNextTo((entity, xform), targetXform.ParentUid);
        }
    }
}
