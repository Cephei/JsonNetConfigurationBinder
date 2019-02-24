// Copyright (c) Daniel Gioulakis.
// Licensed under the Apache License, Version 2.0.
// The majority of this source comes from Microsoft's default ConfigurationBinder.
// This code has not been thoroughly tested and was done as a POC. Please be aware.
// The majority of this code focuses on being able to convert IConfigurationSection into
// a (de)serializable JToken tree structure, thus allowing you to leverage Json.net's
// JsonConverter capabilities during binding.

// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Microsoft.Extensions.Configuration
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Linq;
    using System.Reflection;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Newtonsoft.Json.Serialization;

    /// <summary>
    /// Static helper class that allows binding strongly typed objects to configuration values.
    /// </summary>
    public static class JsonNetConfigurationBinder
    {
        private static JsonSerializer _serializer = JsonSerializer.Create(
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });
        
        /// <summary>
        /// Attempts to bind the given object instance to the configuration section specified by the key by matching property names against configuration keys recursively.
        /// </summary>
        /// <param name="configuration">The configuration instance to bind.</param>
        /// <param name="key">The key of the configuration section to bind.</param>
        /// <param name="instance">The object to bind.</param>
        public static void BindJson(this IConfiguration configuration, string key, object instance)
            => BindJson(configuration.GetSection(key), instance);

        /// <summary>
        /// Attempts to bind the given object instance to configuration values by matching property names against configuration keys recursively.
        /// </summary>
        /// <param name="configuration">The configuration instance to bind.</param>
        /// <param name="instance">The object to bind.</param>
        public static void BindJson(this IConfiguration configuration, object instance)
            => BindJson(configuration, instance, o => { });

        /// <summary>
        /// Attempts to bind the given object instance to configuration values by matching property names against configuration keys recursively.
        /// </summary>
        /// <param name="configuration">The configuration instance to bind.</param>
        /// <param name="instance">The object to bind.</param>
        /// <param name="configureOptions">Configures the binder options.</param>
        public static void BindJson(this IConfiguration configuration, object instance, Action<BinderOptions> configureOptions)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            if (instance != null)
            {
                var options = new BinderOptions();
                configureOptions?.Invoke(options);
                BindInstance(instance.GetType(), instance, configuration, options);
            }
        }

        private static void BindNonScalar(this IConfiguration configuration, object instance, BinderOptions options)
        {
            if (instance != null)
            {
                foreach (var property in GetAllProperties(instance.GetType().GetTypeInfo()))
                {
                    BindProperty(property, instance, configuration, options);
                }
            }
        }

        private static void BindProperty(PropertyInfo property, object instance, IConfiguration config, BinderOptions options)
        {
            // We don't support set only, non public, or indexer properties
            if (property.GetMethod == null ||
                (!options.BindNonPublicProperties && !property.GetMethod.IsPublic) ||
                property.GetMethod.GetParameters().Length > 0)
            {
                return;
            }

            var propertyValue = property.GetValue(instance);
            var hasSetter = property.SetMethod != null && (property.SetMethod.IsPublic || options.BindNonPublicProperties);

            if (propertyValue == null && !hasSetter)
            {
                // Property doesn't have a value and we cannot set it so there is no
                // point in going further down the graph
                return;
            }

            var jsonConverter = property.GetCustomAttribute<JsonConverterAttribute>();
            if (jsonConverter != null)
            {
                var section = config.GetSection(property.Name);
                var jsonBuilder = section.ToJson();
                var converter = (JsonConverter) Activator.CreateInstance(jsonConverter.ConverterType);
                var reader = new JTokenReader(jsonBuilder);
                while (reader.Read())
                {
                    if (reader.Value == null)
                        continue;

                    // find the property's name
                    if (reader.Value is string &&
                        string.Equals(reader.Value.ToString(), property.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        reader.Read(); // move to the next JToken to get the value for conversion
                        propertyValue = converter.ReadJson(reader, instance.GetType(), instance, _serializer);
                    }
                }
            }
            else
            {
                propertyValue = BindInstance(property.PropertyType, propertyValue, config.GetSection(property.Name), options);
            }

            if (propertyValue != null && hasSetter)
            {
                property.SetValue(instance, propertyValue);
            }
        }

        private static JContainer ToJson(
            this IConfigurationSection section,
            JContainer builder = null)
        {
            
            if (section == null)
                return builder;

            if (string.IsNullOrWhiteSpace(section.Key))
                return builder;
            
            if (builder == null)
                builder = new JObject();

            var children = section.GetChildren().ToList();
            if (children.Any())
            {
                /* Converting an IConfigurationSection to JTokens is not trivial since
                 * Microsoft does not distinguish the node type in the tree. I've only
                 * added logic that assumes everything is an object and not array.
                 *
                 * One way to detect arrays would be to see if all the children
                 * have integers for their IConfigurationSection.Key. Microsoft uses the
                 * same instance type regardless, but sets the keys to be sequential ints.
                 *
                 * e.g.
                 * {
                 *   "someSection": [
                 *     "val1",
                 *     "val2"
                 *   ]
                 * }
                 *
                 * someSection is a ConfigurationSection with null Value, but hasChildren
                 * each element in someSection.GetChildren() is a ConfigurationSection
                 * each element has a Key of int, and Value of the json string value
                 * 
                 */
                var child = new JObject();
                foreach (var childSection in children)
                {
                    childSection.ToJson(child);
                }
                
                builder.Add(new JProperty(section.Key, child));
            }
            else if (builder is JArray) // THIS REMAINS UNTESTED
            {
                builder.Add(JToken.FromObject(section.Value));
            }
            else
            {
                builder.Add(new JProperty(section.Key, JToken.FromObject(section.Value)));
            }

            return builder;
        }

        private static object BindToCollection(TypeInfo typeInfo, IConfiguration config, BinderOptions options)
        {
            var type = typeof(List<>).MakeGenericType(typeInfo.GenericTypeArguments[0]);
            var instance = Activator.CreateInstance(type);
            BindCollection(instance, type, config, options);
            return instance;
        }

        // Try to create an array/dictionary instance to back various collection interfaces
        private static object AttemptBindToCollectionInterfaces(Type type, IConfiguration config, BinderOptions options)
        {
            var typeInfo = type.GetTypeInfo();

            if (!typeInfo.IsInterface)
            {
                return null;
            }

            var collectionInterface = FindOpenGenericInterface(typeof(IReadOnlyList<>), type);
            if (collectionInterface != null)
            {
                // IEnumerable<T> is guaranteed to have exactly one parameter
                return BindToCollection(typeInfo, config, options);
            }

            collectionInterface = FindOpenGenericInterface(typeof(IReadOnlyDictionary<,>), type);
            if (collectionInterface != null)
            {
                var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeInfo.GenericTypeArguments[0], typeInfo.GenericTypeArguments[1]);
                var instance = Activator.CreateInstance(dictionaryType);
                BindDictionary(instance, dictionaryType, config, options);
                return instance;
            }

            collectionInterface = FindOpenGenericInterface(typeof(IDictionary<,>), type);
            if (collectionInterface != null)
            {
                var instance = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeInfo.GenericTypeArguments[0], typeInfo.GenericTypeArguments[1]));
                BindDictionary(instance, collectionInterface, config, options);
                return instance;
            }

            collectionInterface = FindOpenGenericInterface(typeof(IReadOnlyCollection<>), type);
            if (collectionInterface != null)
            {
                // IReadOnlyCollection<T> is guaranteed to have exactly one parameter
                return BindToCollection(typeInfo, config, options);
            }

            collectionInterface = FindOpenGenericInterface(typeof(ICollection<>), type);
            if (collectionInterface != null)
            {
                // ICollection<T> is guaranteed to have exactly one parameter
                return BindToCollection(typeInfo, config, options);
            }

            collectionInterface = FindOpenGenericInterface(typeof(IEnumerable<>), type);
            if (collectionInterface != null)
            {
                // IEnumerable<T> is guaranteed to have exactly one parameter
                return BindToCollection(typeInfo, config, options);
            }

            return null;
        }

        private static object BindInstance(Type type, object instance, IConfiguration config, BinderOptions options)
        {
            // if binding IConfigurationSection, break early
            if (type == typeof(IConfigurationSection))
            {
                return config;
            }

            var section = config as IConfigurationSection;
            var configValue = section?.Value;
            object convertedValue;
            Exception error;
            if (configValue != null && TryConvertValue(type, configValue, out convertedValue, out error))
            {
                if (error != null)
                {
                    throw error;
                }

                // Leaf nodes are always reinitialized
                return convertedValue;
            }
            
            if (config != null && config.GetChildren().Any())
            {
                // If we don't have an instance, try to create one
                if (instance == null)
                {
                    // We are already done if binding to a new collection instance worked
                    instance = AttemptBindToCollectionInterfaces(type, config, options);
                    if (instance != null)
                    {
                        return instance;
                    }

                    instance = CreateInstance(type);
                }

                // See if its a Dictionary
                var collectionInterface = FindOpenGenericInterface(typeof(IDictionary<,>), type);
                if (collectionInterface != null)
                {
                    BindDictionary(instance, collectionInterface, config, options);
                }
                else if (type.IsArray)
                {
                    instance = BindArray((Array)instance, config, options);
                }
                else
                {
                    // See if its an ICollection
                    collectionInterface = FindOpenGenericInterface(typeof(ICollection<>), type);
                    if (collectionInterface != null)
                    {
                        BindCollection(instance, collectionInterface, config, options);
                    }
                    // Something else
                    else
                    {
                        BindNonScalar(config, instance, options);
                    }
                }
            }

            return instance;
        }

        private static object CreateInstance(Type type)
        {
            var typeInfo = type.GetTypeInfo();

            if (typeInfo.IsInterface || typeInfo.IsAbstract)
            {
                throw new InvalidOperationException();
            }

            if (type.IsArray)
            {
                if (typeInfo.GetArrayRank() > 1)
                {
                    throw new InvalidOperationException();
                }

                return Array.CreateInstance(typeInfo.GetElementType(), 0);
            }

            var hasDefaultConstructor = typeInfo.DeclaredConstructors.Any(ctor => ctor.IsPublic && ctor.GetParameters().Length == 0);
            if (!hasDefaultConstructor)
            {
                throw new InvalidOperationException();
            }

            try
            {
                return Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException();
            }
        }

        private static void BindDictionary(object dictionary, Type dictionaryType, IConfiguration config, BinderOptions options)
        {
            var typeInfo = dictionaryType.GetTypeInfo();

            // IDictionary<K,V> is guaranteed to have exactly two parameters
            var keyType = typeInfo.GenericTypeArguments[0];
            var valueType = typeInfo.GenericTypeArguments[1];
            var keyTypeIsEnum = keyType.GetTypeInfo().IsEnum;

            if (keyType != typeof(string) && !keyTypeIsEnum)
            {
                // We only support string and enum keys
                return;
            }

            var setter = typeInfo.GetDeclaredProperty("Item");
            foreach (var child in config.GetChildren())
            {
                var item = BindInstance(
                    type: valueType,
                    instance: null,
                    config: child,
                    options: options);
                if (item != null)
                {
                    if (keyType == typeof(string))
                    {
                        var key = child.Key;
                        setter.SetValue(dictionary, item, new object[] { key });
                    }
                    else if (keyTypeIsEnum)
                    {
                        var key = Enum.Parse(keyType, child.Key);
                        setter.SetValue(dictionary, item, new object[] { key });
                    }
                }
            }
        }

        private static void BindCollection(object collection, Type collectionType, IConfiguration config, BinderOptions options)
        {
            var typeInfo = collectionType.GetTypeInfo();

            // ICollection<T> is guaranteed to have exactly one parameter
            var itemType = typeInfo.GenericTypeArguments[0];
            var addMethod = typeInfo.GetDeclaredMethod("Add");

            foreach (var section in config.GetChildren())
            {
                try
                {
                    var item = BindInstance(
                        type: itemType,
                        instance: null,
                        config: section,
                        options: options);
                    if (item != null)
                    {
                        addMethod.Invoke(collection, new[] { item });
                    }
                }
                catch
                {
                }
            }
        }

        private static Array BindArray(Array source, IConfiguration config, BinderOptions options)
        {
            var children = config.GetChildren().ToArray();
            var arrayLength = source.Length;
            var elementType = source.GetType().GetElementType();
            var newArray = Array.CreateInstance(elementType, arrayLength + children.Length);

            // binding to array has to preserve already initialized arrays with values
            if (arrayLength > 0)
            {
                Array.Copy(source, newArray, arrayLength);
            }

            for (int i = 0; i < children.Length; i++)
            {
                try
                {
                    var item = BindInstance(
                        type: elementType,
                        instance: null,
                        config: children[i],
                        options: options);
                    if (item != null)
                    {
                        newArray.SetValue(item, arrayLength + i);
                    }
                }
                catch
                {
                }
            }

            return newArray;
        }

        private static bool TryConvertValue(Type type, string value, out object result, out Exception error)
        {
            error = null;
            result = null;
            if (type == typeof(object))
            {
                result = value;
                return true;
            }
  
            if (type.GetTypeInfo().IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                if (string.IsNullOrEmpty(value))
                {
                    return true;
                }
                return TryConvertValue(Nullable.GetUnderlyingType(type), value, out result, out error);
            }
            
            var converter = TypeDescriptor.GetConverter(type);
            if (converter.CanConvertFrom(typeof(string)))
            {
                try
                {
                    result = converter.ConvertFromInvariantString(value);
                }
                catch (Exception ex)
                {
                    error = new InvalidOperationException();
                }
                return true;
            }
  
            return false;
        }

        private static Type FindOpenGenericInterface(Type expected, Type actual)
        {
            var actualTypeInfo = actual.GetTypeInfo();
            if(actualTypeInfo.IsGenericType && 
                actual.GetGenericTypeDefinition() == expected)
            {
                return actual;
            } 
             
            var interfaces = actualTypeInfo.ImplementedInterfaces;
            foreach (var interfaceType in interfaces)
            {
                if (interfaceType.GetTypeInfo().IsGenericType &&
                    interfaceType.GetGenericTypeDefinition() == expected)
                {
                    return interfaceType;
                }
            }
            return null;
        }

        private static IEnumerable<PropertyInfo> GetAllProperties(TypeInfo type)
        {
            var allProperties = new List<PropertyInfo>();

            do
            {
                allProperties.AddRange(type.DeclaredProperties);
                type = type.BaseType.GetTypeInfo();
            }
            while (type != typeof(object).GetTypeInfo());

            return allProperties;
        }
    }
}
