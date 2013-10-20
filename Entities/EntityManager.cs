﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Neon.Utility;
using Neon.Collections;

namespace Neon.Entities {
    /// <summary>
    /// A set of operations that are used for managing entities.
    /// </summary>
    public interface IEntityManager {
        /// <summary>
        /// Code to call when we do our next update.
        /// </summary>
        //event Action OnNextUpdate;

        /// <summary>
        /// Our current update number. Useful for debugging purposes.
        /// </summary>
        int UpdateNumber {
            get;
        }

        /// <summary>
        /// Registers the given system with the EntityManager.
        /// </summary>
        void AddSystem(ISystem system);

        /// <summary>
        /// Updates the world. State changes (entity add, entity remove, ...) are propogated to the different
        /// registered listeners. Update listeners will be called and the given commands will be executed.
        /// </summary>
        void UpdateWorld(IEnumerable<IStructuredInput> commands);

        /// <summary>
        /// Registers the given entity with the world.
        /// </summary>
        /// <param name="instance">The instance to add</param>
        void AddEntity(IEntity entity);

        /// <summary>
        /// Destroys the given entity.
        /// </summary>
        /// <param name="instance">The entity instance to remove</param>
        void RemoveEntity(IEntity entity);

        /// <summary>
        /// Singleton entity that contains global data
        /// </summary>
        IEntity SingletonEntity {
            get;
        }
    }

    /// <summary>
    /// The EntityManager requires an associated Entity which is not injected into the
    /// EntityManager.
    /// </summary>
    public class EntityManager : IEntityManager {
        /// <summary>
        /// If true, then the EntityManager will emit lots of log messages.
        /// </summary>
        public bool EnableLogs = false;

        private void DoLog(string message, params object[] args) {
            if (EnableLogs) {
                Log.Info("(" + UpdateNumber + ") " + message, args);
            }
        }

        /// <summary>
        /// The list of active Entities in the world.
        /// </summary>
        private UnorderedList<IEntity> _entities = new UnorderedList<IEntity>();

        /// <summary>
        /// A list of Entities that need to be added to the world.
        /// </summary>
        private List<Entity> _entitiesToAdd = new List<Entity>();

        /// <summary>
        /// A list of Entities that need to be removed from the world.
        /// </summary>
        private List<Entity> _entitiesToRemove = new List<Entity>();

        /// <summary>
        /// A double buffered list of Entities that have been modified.
        /// </summary>
        private BufferedItem<List<Entity>> _entitiesWithModifications = new BufferedItem<List<Entity>>();

        /// <summary>
        /// Entities which have had their states changed.
        /// </summary>
        private LinkedList<Entity> _entitiesWithStateChanges = new LinkedList<Entity>();

        private List<System> _allSystems = new List<System>();
        private List<System> _systemsWithUpdateTriggers = new List<System>();
        private List<System> _systemsWithInputTriggers = new List<System>();

        class ModifiedTrigger {
            public ITriggerModified Trigger;
            public Filter Filter;

            public ModifiedTrigger(ITriggerModified trigger) {
                Trigger = trigger;
                Filter = new Filter(trigger.ComputeEntityFilter());
            }
        }
        private List<ModifiedTrigger> _modifiedTriggers = new List<ModifiedTrigger>();

        private List<ITriggerGlobalPreUpdate> _globalPreUpdateTriggers = new List<ITriggerGlobalPreUpdate>();
        private List<ITriggerGlobalPostUpdate> _globalPostUpdateTriggers = new List<ITriggerGlobalPostUpdate>();
        private List<ITriggerGlobalInput> _globalInputTriggers = new List<ITriggerGlobalInput>();

        /// <summary>
        /// The key we use to access unordered list metadata from the entity.
        /// </summary>
        private static MetadataKey _entityUnorderedListMetadataKey = Entity.MetadataRegistry.GetKey();

        /// <summary>
        /// The key we use to access our modified listeners for the entity
        /// </summary>
        private static MetadataKey _entityModifiedListenersKey = Entity.MetadataRegistry.GetKey();

        public IEntity SingletonEntity {
            get;
            set;
        }

        public EntityManager(IEntity singletonEntity) {
            SingletonEntity = singletonEntity;
        }

        /// <summary>
        /// Registers the given system with the EntityManager.
        /// </summary>
        public void AddSystem(ISystem baseSystem) {
            Contract.Requires(_entities.Length == 0, "Cannot add a trigger after entities have been added");
            DoLog("Adding system {0}", baseSystem);

            if (baseSystem is ITriggerBaseFilter) {
                System system = new System((ITriggerBaseFilter)baseSystem);
                _allSystems.Add(system);

                if (baseSystem is ITriggerLifecycle) {
                    var lifecycle = (ITriggerLifecycle)baseSystem;
                    system.OnAddedToCache += entity => {
                        lifecycle.OnAdded(entity);
                    };
                    system.OnRemovedFromCache += entity => {
                        lifecycle.OnRemoved(entity);
                    };
                }
                if (baseSystem is ITriggerUpdate) {
                    _systemsWithUpdateTriggers.Add(system);
                }
                if (baseSystem is ITriggerModified) {
                    var modified = (ITriggerModified)baseSystem;
                    _modifiedTriggers.Add(new ModifiedTrigger(modified));
                }
                if (baseSystem is ITriggerInput) {
                    _systemsWithInputTriggers.Add(system);
                }
            }


            if (baseSystem is ITriggerGlobalPreUpdate) {
                _globalPreUpdateTriggers.Add((ITriggerGlobalPreUpdate)baseSystem);
            }
            if (baseSystem is ITriggerGlobalPostUpdate) {
                _globalPostUpdateTriggers.Add((ITriggerGlobalPostUpdate)baseSystem);
            }
            if (baseSystem is ITriggerGlobalInput) {
                _globalInputTriggers.Add((ITriggerGlobalInput)baseSystem);
            }
        }

        public int UpdateNumber {
            get;
            private set;
        }

        /// <summary>
        /// Updates the world. State changes (entity add, entity remove, ...) are propagated to the different
        /// registered listeners. Update listeners will be called and the given commands will be executed.
        /// </summary>
        public void UpdateWorld(IEnumerable<IStructuredInput> commands) {
            ++UpdateNumber;

            // we add/remove entities here because an entity could be in the modified list (which is used in the CallModifiedMethods)

            InvokeAddEntities();
            InvokeRemoveEntities();

            InvokeEntityDataStateChanges();
            InvokeModifications();

            InvokeOnCommandMethods(commands);
            InvokeUpdateMethods();

            // update the singleton data
            SingletonEntity.ApplyModifications();

            // we destroy entities so that disappear quickly (and not waiting for the start of the next update)
            //DoDestroyEntities();
            //DoRemoveEntities();
        }

        /// <summary>
        /// Dispatches all Entity modifications and calls the relevant methods.
        /// </summary>
        private void InvokeModifications() {
            // Call the Modified triggers that are associated with the given entity.
            Action<Entity> invokeModifiedMethods = modifiedEntity => {
                // call the InvokeOnModified functions - *user code*
                // we store which methods are relevant to the entity in the entities metadata for performance reasons
                List<ITriggerModified> triggers = (List<ITriggerModified>)modifiedEntity.Metadata[_entityModifiedListenersKey];
                for (int i = 0; i < _modifiedTriggers.Count; ++i) {
                    DoLog("Modification check to see if {0} is interested in {1}", _modifiedTriggers[i].Trigger, modifiedEntity);
                    if (_modifiedTriggers[i].Filter.ModificationCheck(modifiedEntity)) {
                        DoLog("Invoking {0} on {1}", _modifiedTriggers[i].Trigger, modifiedEntity);
                        _modifiedTriggers[i].Trigger.OnModified(modifiedEntity);
                    }
                }
            };


            // Alert the modified listeners that the entity has been modified
            List<Entity> modifiedEntities = _entitiesWithModifications.Swap();
            foreach (var modified in modifiedEntities) {
                DoLog("Applying modifications to {0}", modified);
                // apply the modifications to the entity; ie, dispatch changes
                modified.ApplyModifications();

                // call the InvokeOnModified functions - *user code*
                invokeModifiedMethods(modified);
            }
            modifiedEntities.Clear();
        }

        /// <summary>
        /// Dispatches the set of commands to all [InvokeOnCommand] methods.
        /// </summary>
        private void InvokeOnCommandMethods(IEnumerable<IStructuredInput> inputSequence) {
            // Call the OnCommand methods - *user code*
            foreach (var input in inputSequence) {
                for (int i = 0; i < _globalInputTriggers.Count; ++i) {
                    var trigger = _globalInputTriggers[i];
                    DoLog("Checking {0} for input {1}", trigger, input);
                    if (trigger.IStructuredInputType.IsInstanceOfType(input)) {
                        DoLog("Invoking {0} on input {1}", trigger, input);
                        trigger.OnGlobalInput(input, SingletonEntity);
                    }
                }

                for (int i = 0; i < _systemsWithInputTriggers.Count; ++i) {
                    System system = _systemsWithInputTriggers[i];
                    ITriggerInput trigger = (ITriggerInput)system.Trigger;

                    for (int j = 0; j < system.CachedEntities.Length; ++j) {
                        IEntity entity = system.CachedEntities[j];
                        if (entity.Enabled) {
                            trigger.OnInput(input, entity);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Calls all [InvokeBeforeUpdate], [InvokeOnUpdate], and [InvokeAfterUpdate] methods.
        /// </summary>
        private void InvokeUpdateMethods() {
            // Call the BeforeUpdate methods - *user code*
            for (int i = 0; i < _globalPreUpdateTriggers.Count; ++i) {
                _globalPreUpdateTriggers[i].OnGlobalPreUpdate(SingletonEntity);
            }

            // Call the OnUpdate methods - *user code*
            for (int i = 0; i < _systemsWithUpdateTriggers.Count; ++i) {
                System system = _systemsWithUpdateTriggers[i];
                ITriggerUpdate trigger = (ITriggerUpdate)system.Trigger;

                for (int j = 0; j < system.CachedEntities.Length; ++j) {
                    IEntity entity = system.CachedEntities[j];
                    if (entity.Enabled) {
                        trigger.OnUpdate(entity);
                    }
                }
            }

            // Call the AfterUpdate methods - *user code*
            for (int i = 0; i < _globalPostUpdateTriggers.Count; ++i) {
                _globalPostUpdateTriggers[i].OnGlobalPostUpdate(SingletonEntity);
            }
        }

        private void InvokeAddEntities() {
            // Add entities
            for (int i = 0; i < _entitiesToAdd.Count; ++i) {
                Entity toAdd = _entitiesToAdd[i];
                DoLog("Adding {0}", toAdd);

                toAdd.Show();

                toAdd.AddedToEntityManager(this);

                // register listeners
                toAdd.OnModified += OnEntityModified;
                toAdd.OnDataStateChanged += OnEntityDataStateChanged;

                // apply initialization changes
                toAdd.ApplyModifications();

                // ensure it contains metadata for our keys
                toAdd.Metadata[_entityUnorderedListMetadataKey] = new UnorderedListMetadata();
                toAdd.Metadata[_entityModifiedListenersKey] = new List<ITriggerModified>();

                // add it our list of active entities
                _entities.Add(toAdd, (UnorderedListMetadata)toAdd.Metadata[_entityUnorderedListMetadataKey]);

                // update optimization caches -- *USER CODE*
                //InvokeDataStateChanges(toAdd);
                if (_entitiesWithStateChanges.Contains(toAdd) == false) {
                    _entitiesWithStateChanges.AddLast(toAdd);
                }
            }

            _entitiesToAdd.Clear();
        }

        private void InvokeEntityDataStateChanges() {
            // Note that this loop is carefully constructed
            // It has to handle a couple of (difficult) things;
            // first, it needs to support the item that is being
            // iterated being removed, and secondly, it needs to
            // support more items being added to it as it iterates
            LinkedListNode<Entity> it = _entitiesWithStateChanges.Last;
            while (it != null) {
                var curr = it;
                it = it.Previous;
                var entity = curr.Value;

                DoLog("Doing data state changes on {0}", entity);
                // update data state changes and if there are no more updates needed,
                // then remove it from the dispatch list
                if (entity.DataStateChangeUpdate() == false) {
                    DoLog("No more state changes requested for {0}", entity);
                    _entitiesWithStateChanges.Remove(curr);
                }

                // update the entity's internal trigger cache
                List<ITriggerModified> triggers = (List<ITriggerModified>)entity.Metadata[_entityModifiedListenersKey];
                triggers.Clear();
                for (int i = 0; i < _modifiedTriggers.Count; ++i) {
                    if (_modifiedTriggers[i].Filter.Check(entity)) {
                        triggers.Add(_modifiedTriggers[i].Trigger);
                    }
                }

                // update the caches on the entity and call user code - *user code*
                for (int i = 0; i < _allSystems.Count; ++i) {
                    var change = _allSystems[i].UpdateCache(entity);
                    DoLog("Updated cache in {0} for {1}; change was {2}", _allSystems[i].Trigger, entity, change);
                }
            }
        }

        private void InvokeRemoveEntities() {
            // Destroy entities
            for (int i = 0; i < _entitiesToRemove.Count; ++i) {
                Entity toDestroy = _entitiesToRemove[i];
                toDestroy.RemoveAllData();

                DoLog("Removing {0}", toDestroy);

                // remove listeners
                toDestroy.OnModified -= OnEntityModified;
                toDestroy.OnDataStateChanged -= OnEntityDataStateChanged;

                // remove the entity from caches
                for (int j = 0; j < _allSystems.Count; ++j) {
                    _allSystems[j].Remove(toDestroy);
                }
                List<ITriggerModified> triggers = (List<ITriggerModified>)toDestroy.Metadata[_entityModifiedListenersKey];
                triggers.Clear();

                // remove the entity from the list of entities
                _entities.Remove(toDestroy, (UnorderedListMetadata)toDestroy.Metadata[_entityUnorderedListMetadataKey]);
                toDestroy.RemovedFromEntityManager();
            }
            _entitiesToRemove.Clear();
        }

        /// <summary>
        /// Registers the given entity with the world.
        /// </summary>
        /// <param name="instance">The instance to add</param>
        public void AddEntity(IEntity instance) {
            DoLog("AddEntity({0}) called", instance);
            Entity entity = (Entity)instance;
            _entitiesToAdd.Add(entity);
            _entitiesWithStateChanges.AddLast(entity);
            entity.Hide();
        }

        /// <summary>
        /// Removes the given entity from the world.
        /// </summary>
        /// <param name="instance">The entity instance to remove</param>
        public void RemoveEntity(IEntity instance) {
            DoLog("RemoveEntity({0}) called", instance);
            Entity entity = (Entity)instance;
            if (_entitiesToRemove.Contains(entity) == false) {
                _entitiesToRemove.Add(entity);
                entity.Hide();
            }
        }

        /// <summary>
        /// Called when an Entity has been modified.
        /// </summary>
        private void OnEntityModified(Entity sender) {
            DoLog("Got modification notification for {0}... checking for duplicates", sender);
            if (_entitiesWithModifications.Get().Contains(sender) == false) {
                DoLog("Adding {0} to modification list", sender);
                _entitiesWithModifications.Get().Add(sender);
            }
        }

        /// <summary>
        /// Called when an entity has data state changes
        /// </summary>
        private void OnEntityDataStateChanged(Entity sender) {
            DoLog("Got data state change for {0}... checking for duplicates", sender);
            if (_entitiesWithStateChanges.Contains(sender) == false) {
                DoLog("Adding {0} to date state change list", sender);
                _entitiesWithStateChanges.AddLast(sender);
            }
        }
    }
}