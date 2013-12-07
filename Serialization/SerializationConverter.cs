﻿using Neon.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Neon.Serialization {
    /// <summary>
    /// Converts a given serialized value into an instance of an object.
    /// </summary>
    /// <param name="serializedData">The serialized value to convert.</param>
    public delegate object Importer(SerializedData serializedData);

    /// <summary>
    /// Deserialize a given object instance into a serialized value.
    /// </summary>
    /// <param name="instance">The object instance to serialize.</param>
    public delegate SerializedData Exporter(object instance);

    /// <summary>
    /// Converts types to and from SerializedDatas.
    /// </summary>
    public class SerializationConverter {
        /// <summary>
        /// Custom importers that convert SerializedData directly into object instances.
        /// </summary>
        private Dictionary<Type, Importer> _importers = new Dictionary<Type, Importer>();

        /// <summary>
        /// Custom exporters that convert object instances directly into SerializedData.
        /// </summary>
        private Dictionary<Type, Exporter> _exporters = new Dictionary<Type, Exporter>();

        /// <summary>
        /// The import graph that we are currently using. If we do not currently have an import
        /// graph, then this value is set to null. We only have an import graph when ImportGraph is
        /// being called instead of Import.
        /// </summary>
        private SerializationGraphImporter _importGraph;

        /// <summary>
        /// The export graph that we are currently using. If we do not currently have an export
        /// graph, then this value is set to null. We only have an export graph when ExportGraph is
        /// being called instead of Export.
        /// </summary>
        private SerializationGraphExporter _exportGraph;

        /// <summary>
        /// Initializes a new instance of the SerializationConverter class. Adds default importers
        /// and exporters by default; the default converters are used for converting the primitive
        /// types that SerializedData maps directly to.
        /// </summary>
        public SerializationConverter(bool addDefaultConverters = true) {
            // Register default converters
            if (addDefaultConverters) {
                // add importers for some of the primitive types
                AddImporter<byte>(data => (byte)data.AsReal.AsInt);
                AddImporter<short>(data => (short)data.AsReal.AsInt);
                AddImporter<int>(data => data.AsReal.AsInt);
                AddImporter<Real>(data => data.AsReal);
                AddImporter<bool>(data => data.AsBool);
                AddImporter<string>(data => data.AsString);
                AddImporter<SerializedData>(data => data);

                // add exporters for some of the more primitive types
                AddExporter<byte>(value => new SerializedData(Real.CreateDecimal(value)));
                AddExporter<short>(value => new SerializedData(Real.CreateDecimal(value)));
                AddExporter<int>(value => new SerializedData(Real.CreateDecimal(value)));
                AddExporter<Real>(value => new SerializedData(value));
                AddExporter<bool>(value => new SerializedData(value));
                AddExporter<string>(value => new SerializedData(value));
                AddExporter<SerializedData>(value => value);
            }
        }

        /// <summary>
        /// Attempt to remove an importer that imports the given type. Returns true if an importer
        /// was found and removed.
        /// </summary>
        public bool RemoveImporter(Type destinationType) {
            return _importers.Remove(destinationType);
        }

        /// <summary>
        /// Attempt to remove an exporter that exports the given type. Returns true if an exporter
        /// was found and removed.
        /// </summary>
        public bool RemoveExporter(Type serializedType) {
            return _exporters.Remove(serializedType);
        }

        /// <summary>
        /// Registers a converter that will convert SerializedData instances to their respective
        /// destination types.
        /// </summary>
        /// <param name="importer">A function that takes instances of serialized data and converts
        /// them to instances of type T.</param>
        public void AddImporter<T>(Func<SerializedData, T> importer) {
            AddImporter(typeof(T), data => importer(data));
        }

        /// <summary>
        /// Registers a converter that will convert SerializedData instances to their respective
        /// destination types.
        /// </summary>
        public void AddImporter(Type destinationType, Importer importer) {
            if (_importers.ContainsKey(destinationType)) {
                throw new InvalidOperationException("There is already a registered importer for type " + destinationType);
            }

            _importers[destinationType] = importer;
        }

        /// <summary>
        /// Adds an exporter that takes instances of data and converts them to serialized data.
        /// </summary>
        /// <param name="exporter">A function that takes instances of data and converts them to
        /// serialized data</param>
        public void AddExporter<T>(Func<T, SerializedData> exporter) {
            AddExporter(typeof(T), instance => exporter((T)instance));
        }

        /// <summary>
        /// Adds an exporter that takes instances of data and converts them to serialized data.
        /// </summary>
        /// <param name="serializedType">The instance type that this exporter can serialize.</param>
        /// <param name="exporter">The exporter itself.</param>
        public void AddExporter(Type serializedType, Exporter exporter) {
            if (_exporters.ContainsKey(serializedType)) {
                throw new InvalidOperationException("There is already a registered exporter for type " + serializedType);
            }

            _exporters[serializedType] = exporter;
        }

        /// <summary>
        /// Converts a general object instance into SerializedData.
        /// </summary>
        /// <remarks>
        /// Though the magic of generic type inference, using this method works nicely and correctly
        /// supports interfaces when the known instance type is not concrete. For example,
        /// Export((iface)inst) will dispatch to Export(typeof(iface), inst) instead of
        /// Export(inst.GetType(), inst). The second one will not import correctly, as the client is
        /// only going to know that the base type of serialized object, not the type itself. In
        /// essence, using a generic limits the information of the Export function to that of the
        /// Import function.
        /// </remarks>
        public SerializedData Export<T>(T instance) {
            return Export(typeof(T), instance);
        }

        public SerializedData ExportGraph(Type type, object instance) {
            if (_exportGraph == null) {
                _exportGraph = new SerializationGraphExporter();

                try {
                    SerializedData data = Export(type, instance);

                    SerializedData result = SerializedData.CreateDictionary();
                    result.AsDictionary["PrimaryData"] = data;
                    result.AsDictionary["SupportGraph"] = _exportGraph.Export(this);

                    return result;
                }
                finally {
                    _exportGraph = null;
                }
            }

            return Export(type, instance);
        }

        public object ImportGraph(Type type, SerializedData data) {
            if (_importGraph == null) {
                _importGraph = new SerializationGraphImporter(data.AsDictionary["SupportGraph"]);

                try {
                    object result = Import(type, data.AsDictionary["PrimaryData"]);
                    _importGraph.RestoreGraph(this);
                    return result;
                }
                finally {
                    _importGraph = null;
                }
            }

            return Import(type, data);
        }

        /// <summary>
        /// Converts a general object instance into SerializedData. Assuming serialization is
        /// working as expected and custom importers/exporters are written correctly, calling Import
        /// on the return value with given will result in an object that is identical to instance.
        /// </summary>
        /// <param name="instanceType">The type to use when we export instance. instance *must* be
        /// an instance of instanceType, though instanceType can be anywhere on the hierarchy for
        /// instance (for example, it could be typeof(object).</param>
        /// <param name="instance">The object instance to export. If it is not null, then it must be
        /// an instance of instanceType</param>
        /// <param name="forceCyclic">If this is true, then object references will not be generated
        /// for the first-level export recursion.</param>
        public SerializedData Export(Type instanceType, object instance, bool forceCyclic = false) {
            Log<SerializationConverter>.Info("Exporting " + instance + " with type " + instanceType);

            // special case for null values
            if (instance == null) {
                return new SerializedData();
            }

            if (instanceType.IsInstanceOfType(instance) == false) {
                throw new ArgumentException("The instance is not an instance of the export type; " +
                    "instance=" + instance + ", instanceType=" + instanceType);
            }

            TypeMetadata metadata = TypeCache.GetMetadata(instanceType);

            // if this object supports cyclic references, then we're just going to export the
            // reference to the actual definition, which will be contained in the graph itself
            if (forceCyclic == false && metadata.SupportsCyclicReferences) {
                if (_exportGraph == null) {
                    throw new InvalidOperationException("Cycle support required in serialization " +
                        "graph; use GraphExport instead of Export");
                }

                return _exportGraph.GetReferenceForObject(instanceType, instance);
            }

            // If there is a user-defined exporter for the given type, then use it instead of doing
            // automated reflection.
            if (_exporters.ContainsKey(instanceType)) {
                return _exporters[instanceType](instance);
            }

            // we just serialize all enums as strings
            if (instanceType.IsEnum) {
                return Export(typeof(string), instance.ToString());
            }

            // There is no user-defined exporting function. We'll have to use reflection to populate
            // the fields of the serialized value and hope that we did a good enough job.

            // If the type needs to support inheritance, and there was not an exporter, then
            // register the automatic one and rerun the export process.
            if (metadata.SupportsInheritance) {
                EnableSupportForInheritance(instanceType);
                return Export(instanceType, instance);
            }

            // Oops, the type requires a custom converter. We can't process this.
            if (metadata.RequiresCustomConverter) {
                throw new RequiresCustomConverterException(instanceType, importing: false);
            }

            // If it's an array or a collection, we have special logic for processing
            if (metadata.IsArray || metadata.IsCollection) {
                List<SerializedData> output = new List<SerializedData>();

                // luckily both arrays and collections are enumerable, so we'll use that interface
                // when outputting the object
                IEnumerable enumerator = (IEnumerable)instance;
                foreach (object item in enumerator) {
                    // make sure we export under the element type of the list; if we don't, and say,
                    // the element type is an interface, and we export under the instance type, then
                    // deserialization will not work as expected (because we'll be trying to
                    // deserialize an interface).
                    output.Add(Export(metadata.ElementType, item));
                }

                return new SerializedData(output);
            }

            // This is not an array or list; populate from reflected properties
            else {
                var dict = new Dictionary<string, SerializedData>();

                for (int i = 0; i < metadata.Properties.Count; ++i) {
                    PropertyMetadata propertyMetadata = metadata.Properties[i];

                    // make sure we export under the storage type of the property; if we don't, and
                    // say, the property is an interface, and we export under the instance type,
                    // then deserialization will not work as expected (because we'll be trying to
                    // deserialize an interface).
                    dict[propertyMetadata.Name] = Export(propertyMetadata.StorageType,
                        propertyMetadata.Read(instance));
                }

                return new SerializedData(dict);
            }
        }

        /// <summary>
        /// Wrapper around Import(Type, SerializedData).
        /// </summary>
        public T Import<T>(SerializedData serializedData) {
            return (T)Import(typeof(T), serializedData);
        }

        /// <summary>
        /// Converts SerializedData into a general object instance. Assuming serialization is
        /// working as expected and custom importers/exporters are written correctly, calling Export
        /// on the return value with given will result in an object that is identical to
        /// serializedData.
        /// </summary>
        /// <param name="type">The type of data to import.</param>
        /// <param name="serializedData">The data to use when importing the object.</param>
        /// <param name="instance">An optional instance of the given type to allocate data
        /// into.</param>
        public object Import(Type type, SerializedData serializedData, object instance = null) {
            Log<SerializationConverter>.Info("Importing " + serializedData.PrettyPrinted +
                " with type " + type);

            // special case for null values
            if (serializedData.IsNull) {
                return null;
            }

            // if the data is an object reference, then we need to lookup the actual definition in
            // the graph and return that
            if (serializedData.IsObjectReference) {
                if (_importGraph == null) {
                    throw new InvalidOperationException("Cycle support required in serialization " +
                        "graph; use GraphImport instead of Import");
                }

                return _importGraph.GetObjectInstance(type, serializedData.AsObjectReference);
            }

            // If there is a user-defined importer for the given type, then use it instead of doing
            // automated reflection.
            if (_importers.ContainsKey(type)) {
                return _importers[type](serializedData);
            }

            // we just import all enums as strings
            if (type.IsEnum) {
                return Enum.Parse(type, serializedData.AsString);
            }

            // There is no user-defined importer function. We'll have to use reflection to populate
            // the fields of the object, and hope that we did a good enough job.

            TypeMetadata metadata = TypeCache.GetMetadata(type);

            // If the type needs to support inheritance, and there was not an importer, then
            // register the automatic one and rerun the import process.
            if (metadata.SupportsInheritance) {
                EnableSupportForInheritance(type);
                return Import(type, serializedData, instance);
            }

            // Oops, the type requires a custom converter. We can't process this.
            if (metadata.RequiresCustomConverter) {
                throw new RequiresCustomConverterException(type, importing: true);
            }

            if (instance == null) {
                instance = metadata.CreateInstance();
            }

            // If it's an array or a collection, we have special logic for processing
            if (metadata.IsArray || metadata.IsCollection) {
                IList<SerializedData> items = serializedData.AsList;

                for (int i = 0; i < items.Count; ++i) {
                    object indexedObject = Import(metadata.ElementType, items[i]);
                    metadata.AppendValue(ref instance, indexedObject, i);
                }
            }

            // This is not an array or list; populate from reflected properties
            else {
                Dictionary<string, SerializedData> serializedDataDict = serializedData.AsDictionary;
                for (int i = 0; i < metadata.Properties.Count; ++i) {
                    PropertyMetadata propertyMetadata = metadata.Properties[i];

                    // deserialize the property
                    string name = propertyMetadata.Name;
                    Type storageType = propertyMetadata.StorageType;

                    // throw if the dictionary is missing a required property
                    if (serializedDataDict.ContainsKey(name) == false) {
                        throw new Exception("There is no key " + name + " in serialized data "
                            + serializedDataDict + " when trying to deserialize an instance of " +
                            type);
                    }

                    object deserialized = Import(storageType, serializedDataDict[name]);

                    // write it into the instance
                    propertyMetadata.Write(instance, deserialized);
                }
            }

            return instance;
        }

        /// <summary>
        /// Adds a custom importer and exporter for the given type so that it can support importing
        /// and exporting derived types. This adds additional overhead (both in time and in space)
        /// to the serialization process and is therefore opt-in. It also consumes the custom
        /// importer and custom exporter for the BaseType.
        /// </summary>
        /// <remarks>
        /// This method is exposed in case the base type cannot be modified but needs to support
        /// inheritance, but is not abstract not an interface.
        /// </remarks>
        /// <param name="interfaceType">The type of object to support inheritance serialization
        /// for</param>
        public void EnableSupportForInheritance(Type interfaceType) {
            // The serialization format is:
            //
            // {
            // DerivedType: "SomeNamespace.Type"
            // DerivedContent: { # looks like the top level for the serialized type }
            // }

            AddImporter(interfaceType, data => {
                Type type = TypeCache.FindType(data.AsDictionary["Type"].AsString);
                return Import(type, data.AsDictionary["Content"]);
            });

            AddExporter(interfaceType, instance => {
                SerializedData data = SerializedData.CreateDictionary();

                // Export the type so we know what type to import as.
                data.AsDictionary["Type"] = new SerializedData(instance.GetType().FullName);

                // we want to make sure we export under the direct instance type, otherwise we'll go
                // into an infinite loop of reexporting the interface metadata.
                data.AsDictionary["Content"] = Export(instance.GetType(), instance);

                return data;
            });
        }
    }
}