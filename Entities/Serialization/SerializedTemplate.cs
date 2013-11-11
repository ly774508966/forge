﻿using Neon.Serialization;
using Neon.Utilities;
using System;
using System.Collections.Generic;

namespace Neon.Entities.Serialization {
    /// <summary>
    /// Serialization specification for a data instance inside of an EntityTemplate.
    /// </summary>
    public class SerializedTemplateData {
        /// <summary>
        /// The fully qualified type of the data instance.
        /// </summary>
        public string DataType;

        /// <summary>
        /// The serialization data for the state initial data state of the template.
        /// </summary>
        public SerializedData State;

        [NonSerialized]
        private Data _dataInstance;

        /// <summary>
        /// Deserializes the TemplateDataJson into a Data instance. The result is cached.
        /// </summary>
        public Data GetDataInstance(SerializationConverter converter) {
            if (_dataInstance == null) {
                _dataInstance = (Data)converter.Import(TypeCache.FindType(DataType), State);
            }

            return _dataInstance;
        }
    }

    /// <summary>
    /// Serialization specification for an EntityTemplate.
    /// </summary>
    public class SerializedTemplate {
        /// <summary>
        /// The pretty name of the template (for debugging).
        /// </summary>
        public string PrettyName;

        /// <summary>
        /// The template's unique id.
        /// </summary>
        public int TemplateId;

        /// <summary>
        /// All data instances inside of the template.
        /// </summary>
        public List<SerializedTemplateData> Data;

        /// <summary>
        /// Loads the template converter that allows templates to be specified by their id.
        /// </summary>
        public static void LoadTemplateConverter(IEnumerable<SerializedTemplate> templates,
            SerializationConverter converter) {

            Dictionary<int, EntityTemplate> _foundTemplates = new Dictionary<int, EntityTemplate>();

            foreach (var templateJson in templates) {
                EntityTemplate template = new EntityTemplate(templateJson.TemplateId,
                    templateJson.PrettyName);

                foreach (var dataJson in templateJson.Data) {
                    template.AddDefaultData(dataJson.GetDataInstance(converter));
                }

                _foundTemplates[templateJson.TemplateId] = template;
            }

            converter.AddImporter(typeof(EntityTemplate), serializedData => {
                EntityTemplate template;

                if (serializedData.IsReal) {
                    int id = serializedData.AsReal.AsInt;
                    if (_foundTemplates.TryGetValue(id, out template)) {
                        return template;
                    }

                    throw new Exception("No such template with id=" + id);
                }

                throw new Exception("Inline template definitions are not supported; must load template by referencing its TemplateId");
            });

            converter.AddExporter(typeof(EntityTemplate), template => {
                return new SerializedData(((EntityTemplate)template).TemplateId);
            });
        }
    }
}