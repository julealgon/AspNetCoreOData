//-----------------------------------------------------------------------------
// <copyright file="DeltaOfT.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.OData.Abstracts;
using Microsoft.AspNetCore.OData.Common;

namespace Microsoft.AspNetCore.OData.Deltas
{
    /// <summary>
    /// A class the tracks changes (i.e. the Delta) for a particular <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">T is the type of the instance this delta tracks changes for.</typeparam>
    [NonValidatingParameterBinding]
    public class Delta<T> : Delta, IDelta, ITypedDelta where T : class
    {
        // cache property accessors for this type and all its derived types.
        private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyAccessor<T>>> _propertyCache
            = new ConcurrentDictionary<Type, Dictionary<string, PropertyAccessor<T>>>();

        private Dictionary<string, PropertyAccessor<T>> _allProperties;
        private List<string> _updatableProperties;

        private HashSet<string> _changedProperties;

        // Nested resources or structures changed at this level.
        private IDictionary<string, object> _deltaNestedResources;

        private T _instance;
        private Type _structuredType;

        private readonly PropertyInfo _dynamicDictionaryPropertyinfo;
        private HashSet<string> _changedDynamicProperties;
        private IDictionary<string, object> _dynamicDictionaryCache;

        /// <summary>
        /// Initializes a new instance of <see cref="Delta{T}"/>.
        /// </summary>
        public Delta()
            : this(typeof(T))
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Delta{T}"/>.
        /// </summary>
        /// <param name="structuralType">The derived entity type or complex type for which the changes would be tracked.
        /// <paramref name="structuralType"/> should be assignable to instances of <typeparamref name="T"/>.
        /// </param>
        public Delta(Type structuralType)
            : this(structuralType, updatableProperties: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Delta{T}"/>.
        /// </summary>
        /// <param name="structuralType">The derived entity type or complex type for which the changes would be tracked.
        /// <paramref name="structuralType"/> should be assignable to instances of <typeparamref name="T"/>.
        /// </param>
        /// <param name="updatableProperties">The set of properties that can be updated or reset. Unknown property
        /// names, including those of dynamic properties, are ignored.</param>
        public Delta(Type structuralType, IEnumerable<string> updatableProperties)
            : this(structuralType, updatableProperties: updatableProperties, dynamicDictionaryPropertyInfo: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Delta{T}"/>.
        /// </summary>
        /// <param name="structuralType">The derived entity type or complex type for which the changes would be tracked.
        /// <paramref name="structuralType"/> should be assignable to instances of <typeparamref name="T"/>.
        /// </param>
        /// <param name="updatableProperties">The set of properties that can be updated or reset. Unknown property
        /// names, including those of dynamic properties, are ignored.</param>
        /// <param name="dynamicDictionaryPropertyInfo">The property info that is used as dictionary of dynamic
        /// properties. <c>null</c> means this entity type is not open.</param>
        public Delta(Type structuralType, IEnumerable<string> updatableProperties,
            PropertyInfo dynamicDictionaryPropertyInfo)
        {
            _dynamicDictionaryPropertyinfo = dynamicDictionaryPropertyInfo;
            Reset(structuralType);
            InitializeProperties(updatableProperties);
        }

        /// <inheritdoc />
        public override DeltaItemKind Kind => DeltaItemKind.Resource;

        /// <inheritdoc/>
        public virtual Type StructuredType => _structuredType;

        /// <inheritdoc/>
        public virtual Type ExpectedClrType => typeof(T);

        /// <summary>
        /// The list of property names that can be updated.
        /// </summary>
        /// <remarks>When the list is modified, any modified properties that were removed from the list are no longer
        /// considered to be changed.</remarks>
        public IList<string> UpdatableProperties => _updatableProperties;

        /// <inheritdoc/>
        public override void Clear()
        {
            Reset(_structuredType);
        }

        /// <inheritdoc/>
        public override bool TrySetPropertyValue(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Error.ArgumentNull(nameof(name));
            }

            if (_dynamicDictionaryPropertyinfo != null)
            {
                // Dynamic property can have the same name as the dynamic property dictionary.
                if (name == _dynamicDictionaryPropertyinfo.Name ||
                    !_allProperties.ContainsKey(name))
                {
                    if (_dynamicDictionaryCache == null)
                    {
                        _dynamicDictionaryCache =
                            GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, _instance, create: true);
                    }

                    _dynamicDictionaryCache[name] = value;
                    _changedDynamicProperties.Add(name);
                    return true;
                }
            }

            if (value is IDelta)
            {
                return TrySetNestedResourceInternal(name, value);
            }
            else
            {
                return TrySetPropertyValueInternal(name, value);
            }
        }

        /// <inheritdoc/>
        public override bool TryGetPropertyValue(string name, out object value)
        {
            if (name == null)
            {
                throw Error.ArgumentNull(nameof(name));
            }

            if (_dynamicDictionaryPropertyinfo != null)
            {
                if (_dynamicDictionaryCache == null)
                {
                    _dynamicDictionaryCache =
                        GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, _instance, create: false);
                }

                if (_dynamicDictionaryCache != null && _dynamicDictionaryCache.TryGetValue(name, out value))
                {
                    return true;
                }
            }

            if (TryGetNestedPropertyValue(name, out value))
            {
                return true;
            }
            else
            {
                // try to retrieve the value of property.
                PropertyAccessor<T> cacheHit;
                if (_allProperties.TryGetValue(name, out cacheHit))
                {
                    value = cacheHit.GetValue(_instance);
                    return true;
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Attempts to get the value of the nested Property called <paramref name="name"/> from the underlying resource.
        /// <remarks>
        /// Only properties that exist on Entity can be retrieved.
        /// Only modified nested properties can be retrieved.
        /// The nested Property type will be <see cref="IDelta"/> of its defined type.
        /// </remarks>
        /// </summary>
        /// <param name="name">The name of the nested Property</param>
        /// <param name="value">The value of the nested Property</param>
        /// <returns><c>True</c> if the Property was found and is a nested Property</returns>
        public bool TryGetNestedPropertyValue(string name, out object value)
        {
            if (name == null)
            {
                throw Error.ArgumentNull(nameof(name));
            }

            if (!_deltaNestedResources.ContainsKey(name))
            {
                value = null;
                return false;
            }

            // This is a nested resource, the value returned must be an IDelta<T>
            // from the dictionary of nested resources to allow the traversal of
            // hierarchies of Delta<T>.
            object deltaNestedResource = _deltaNestedResources[name];

            Contract.Assert(deltaNestedResource != null, "deltaNestedResource != null");
            Contract.Assert(DeltaHelper.IsDeltaOfT(deltaNestedResource.GetType()));

            value = deltaNestedResource;
            return true;
        }

        /// <inheritdoc/>
        public override bool TryGetPropertyType(string name, out Type type)
        {
            if (name == null)
            {
                throw Error.ArgumentNull(nameof(name));
            }

            if (_dynamicDictionaryPropertyinfo != null)
            {
                if (_dynamicDictionaryCache == null)
                {
                    _dynamicDictionaryCache =
                        GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, _instance, create: false);
                }

                object dynamicValue;
                if (_dynamicDictionaryCache != null &&
                    _dynamicDictionaryCache.TryGetValue(name, out dynamicValue))
                {
                    if (dynamicValue == null)
                    {
                        type = null;
                        return false;
                    }

                    type = dynamicValue.GetType();
                    return true;
                }
            }

            PropertyAccessor<T> value;
            if (_allProperties.TryGetValue(name, out value))
            {
                type = value.Property.PropertyType;
                return true;
            }

            type = null;
            return false;
        }

        /// <summary>
        /// Returns the instance that holds all the changes (and original values) being tracked by this Delta.
        /// </summary>
        public T GetInstance()
        {
            return _instance;
        }

        /// <summary>
        /// Returns the known properties that have been modified through this <see cref="Delta"/> as an
        /// <see cref="IEnumerable{T}" /> of property Names.
        /// Includes the structural properties at current level.
        /// Does not include the names of the changed dynamic properties.
        /// </summary>
        public override IEnumerable<string> GetChangedPropertyNames()
        {
            return _changedProperties.Intersect(_updatableProperties).Concat(_deltaNestedResources.Keys);
        }

        /// <summary>
        /// Returns the known properties that have not been modified through this <see cref="Delta"/> as an
        /// <see cref="IEnumerable{T}" /> of property Names. Does not include the names of the changed dynamic
        /// properties.
        /// </summary>
        public override IEnumerable<string> GetUnchangedPropertyNames()
        {
            // UpdatableProperties could include arbitrary strings, filter by _allProperties
            return _updatableProperties.Intersect(_allProperties.Keys).Except(GetChangedPropertyNames());
        }

        /// <summary>
        /// Copies the changed property values from the underlying entity (accessible via <see cref="GetInstance()" />)
        /// to the <paramref name="original"/> entity recursively.
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void CopyChangedValues(T original)
        {
            if (original == null)
            {
                throw Error.ArgumentNull(nameof(original));
            }

            // Delta parameter type cannot be derived type of original
            // to prevent unrecognizable information from being applied to original resource.
            if (!_structuredType.IsAssignableFrom(original.GetType()))
            {
                throw Error.Argument(nameof(original), SRResources.DeltaTypeMismatch, _structuredType, original.GetType());
            }

            RuntimeHelpers.EnsureSufficientExecutionStack();

            // For regular non-structural properties at current level.
            PropertyAccessor<T>[] propertiesToCopy =
                _changedProperties.Intersect(_updatableProperties).Select(s => _allProperties[s]).ToArray();
            foreach (PropertyAccessor<T> propertyToCopy in propertiesToCopy)
            {
                propertyToCopy.Copy(_instance, original);
            }

            CopyChangedDynamicValues(original);

            // For nested resources.
            foreach (string nestedResourceName in _deltaNestedResources.Keys)
            {
                // Patch for each nested resource changed under this T.
                dynamic deltaNestedResource = _deltaNestedResources[nestedResourceName];
                dynamic originalNestedResource = null;
                if (!TryGetPropertyRef(original, nestedResourceName, out originalNestedResource))
                {
                    throw Error.Argument(nestedResourceName, SRResources.DeltaNestedResourceNameNotFound,
                        nestedResourceName, original.GetType());
                }

                if (originalNestedResource == null)
                {
                    // When patching original target of null value, directly set nested resource.
                    dynamic deltaObject = _deltaNestedResources[nestedResourceName];
                    dynamic instance = deltaObject.GetInstance();

                    // Recursively patch up the instance with the nested resources.
                    deltaObject.CopyChangedValues(instance);

                    _allProperties[nestedResourceName].SetValue(original, instance);
                }
                else
                {
                    // Recursively patch the subtree.
                    bool isDeltaType = DeltaHelper.IsDeltaOfT(deltaNestedResource.GetType());
                    Contract.Assert(isDeltaType, nestedResourceName + "'s corresponding value should be Delta<T> type but is not.");

                    deltaNestedResource.CopyChangedValues(originalNestedResource);
                }
            }
        }

        /// <summary>
        /// Copies the unchanged property values from the underlying entity (accessible via <see cref="GetInstance()" />)
        /// to the <paramref name="original"/> entity.
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void CopyUnchangedValues(T original)
        {
            if (original == null)
            {
                throw Error.ArgumentNull(nameof(original));
            }

            if (!_structuredType.IsInstanceOfType(original))
            {
                throw Error.Argument(nameof(original), SRResources.DeltaTypeMismatch, _structuredType, original.GetType());
            }

            IEnumerable<PropertyAccessor<T>> propertiesToCopy = GetUnchangedPropertyNames().Select(s => _allProperties[s]);
            foreach (PropertyAccessor<T> propertyToCopy in propertiesToCopy)
            {
                propertyToCopy.Copy(_instance, original);
            }

            CopyUnchangedDynamicValues(original);
        }

        /// <summary>
        /// Overwrites the <paramref name="original"/> entity with the changes tracked by this Delta.
        /// <remarks>The semantics of this operation are equivalent to a HTTP PATCH operation, hence the name.</remarks>
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void Patch(T original)
        {
            CopyChangedValues(original);
        }

        /// <summary>
        /// Overwrites the <paramref name="original"/> entity with the values stored in this Delta.
        /// <remarks>The semantics of this operation are equivalent to a HTTP PUT operation, hence the name.</remarks>
        /// </summary>
        /// <param name="original">The entity to be updated.</param>
        public void Put(T original)
        {
            CopyChangedValues(original);
            CopyUnchangedValues(original);
        }

        private static void CopyDynamicPropertyDictionary(IDictionary<string, object> source,
            IDictionary<string, object> dest, PropertyInfo dynamicPropertyInfo, T targetEntity)
        {
            Contract.Assert(source != null);
            Contract.Assert(dynamicPropertyInfo != null);
            Contract.Assert(targetEntity != null);

            if (source.Count == 0)
            {
                if (dest != null)
                {
                    dest.Clear();
                }
            }
            else
            {
                if (dest == null)
                {
                    dest = GetDynamicPropertyDictionary(dynamicPropertyInfo, targetEntity, create: true);
                }
                else
                {
                    dest.Clear();
                }

                foreach (KeyValuePair<string, object> item in source)
                {
                    dest.Add(item);
                }
            }
        }

        private static IDictionary<string, object> GetDynamicPropertyDictionary(PropertyInfo propertyInfo,
            T entity, bool create)
        {
            Contract.Assert(propertyInfo != null);
            Contract.Assert(entity != null);

            object propertyValue = propertyInfo.GetValue(entity);
            if (propertyValue != null)
            {
                return (IDictionary<string, object>)propertyValue;
            }

            if (create)
            {
                if (!propertyInfo.CanWrite)
                {
                    throw Error.InvalidOperation(SRResources.CannotSetDynamicPropertyDictionary, propertyInfo.Name,
                            entity.GetType().FullName);
                }
                IDictionary<string, object> newPropertyValue = new Dictionary<string, object>();

                propertyInfo.SetValue(entity, newPropertyValue);
                return newPropertyValue;
            }

            return null;
        }

        /// <summary>
        /// Attempts to get the property by the specified name.
        /// </summary>
        /// <param name="structural">The structural object.</param>
        /// <param name="propertyName">Name of the property.</param>
        /// <param name="propertyRef">Output for property value.</param>
        /// <returns>true if the property is found; false otherwise.</returns>
        private static bool TryGetPropertyRef(T structural, string propertyName,
            out dynamic propertyRef)
        {
            propertyRef = null;
            PropertyInfo propertyInfo = structural.GetType().GetProperty(propertyName);
            if (propertyInfo != null)
            {
                propertyRef = propertyInfo.GetValue(structural, null);
                return true;
            }

            return false;
        }

        private void Reset(Type structuralType)
        {
            if (structuralType == null)
            {
                throw Error.ArgumentNull(nameof(structuralType));
            }

            if (!typeof(T).IsAssignableFrom(structuralType))
            {
                throw Error.InvalidOperation(SRResources.DeltaEntityTypeNotAssignable, structuralType, typeof(T));
            }

            _instance = Activator.CreateInstance(structuralType) as T;
            _changedProperties = new HashSet<string>();
            _deltaNestedResources = new Dictionary<string, object>();
            _structuredType = structuralType;

            _changedDynamicProperties = new HashSet<string>();
            _dynamicDictionaryCache = null;
        }

        private void InitializeProperties(IEnumerable<string> updatableProperties)
        {
            _allProperties = _propertyCache.GetOrAdd(
                _structuredType,
                (backingType) => backingType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => (p.GetSetMethod() != null || TypeHelper.IsCollection(p.PropertyType)) && p.GetGetMethod() != null)
                    .Select<PropertyInfo, PropertyAccessor<T>>(p => new FastPropertyAccessor<T>(p))
                    .ToDictionary(p => p.Property.Name));

            if (updatableProperties != null)
            {
                _updatableProperties = updatableProperties.Intersect(_allProperties.Keys).ToList();
            }
            else
            {
                _updatableProperties = new List<string>(_allProperties.Keys);
            }

            if (_dynamicDictionaryPropertyinfo != null)
            {
                _updatableProperties.Remove(_dynamicDictionaryPropertyinfo.Name);
            }
        }

        // Copy changed dynamic properties and leave the unchanged dynamic properties
        private void CopyChangedDynamicValues(T targetEntity)
        {
            if (_dynamicDictionaryPropertyinfo == null)
            {
                return;
            }

            if (_dynamicDictionaryCache == null)
            {
                _dynamicDictionaryCache =
                    GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, _instance, create: false);
            }

            IDictionary<string, object> fromDictionary = _dynamicDictionaryCache;
            if (fromDictionary == null)
            {
                return;
            }

            IDictionary<string, object> toDictionary =
                GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, targetEntity, create: false);

            IDictionary<string, object> tempDictionary = toDictionary != null
                ? new Dictionary<string, object>(toDictionary)
                : new Dictionary<string, object>();

            foreach (string dynamicPropertyName in _changedDynamicProperties)
            {
                object dynamicPropertyValue = fromDictionary[dynamicPropertyName];

                // a dynamic property value equal to null, it means to remove this dynamic property
                if (dynamicPropertyValue == null)
                {
                    tempDictionary.Remove(dynamicPropertyName);
                }
                else
                {
                    if (dynamicPropertyValue is IDelta)
                    {
                        dynamic deltaObject = dynamicPropertyValue;
                        dynamic instance = deltaObject.GetInstance();

                        deltaObject.CopyChangedValues(instance);
                        tempDictionary[dynamicPropertyName] = instance;
                    }
                    else
                    {
                        tempDictionary[dynamicPropertyName] = dynamicPropertyValue;
                    }
                }
            }

            CopyDynamicPropertyDictionary(tempDictionary, toDictionary, _dynamicDictionaryPropertyinfo,
                targetEntity);
        }

        // Missing dynamic structural properties MUST be removed or set to null in *Put*
        private void CopyUnchangedDynamicValues(T targetEntity)
        {
            if (_dynamicDictionaryPropertyinfo == null)
            {
                return;
            }

            if (_dynamicDictionaryCache == null)
            {
                _dynamicDictionaryCache =
                    GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, _instance, create: false);
            }

            IDictionary<string, object> toDictionary =
                    GetDynamicPropertyDictionary(_dynamicDictionaryPropertyinfo, targetEntity, create: false);

            if (_dynamicDictionaryCache == null)
            {
                if (toDictionary != null)
                {
                    toDictionary.Clear();
                }
            }
            else
            {
                IDictionary<string, object> tempDictionary = toDictionary != null
                    ? new Dictionary<string, object>(toDictionary)
                    : new Dictionary<string, object>();

                List<string> removedSet = tempDictionary.Keys.Except(_changedDynamicProperties).ToList();

                foreach (string name in removedSet)
                {
                    tempDictionary.Remove(name);
                }

                CopyDynamicPropertyDictionary(tempDictionary, toDictionary, _dynamicDictionaryPropertyinfo,
                    targetEntity);
            }
        }

        private bool TrySetPropertyValueInternal(string name, object value)
        {
            Debug.Assert(name != null, "Argument name is null");

            if (!(_allProperties.ContainsKey(name) && _updatableProperties.Contains(name)))
            {
                return false;
            }

            PropertyAccessor<T> cacheHit = _allProperties[name];

            if (value == null && !cacheHit.Property.PropertyType.IsNullable())
            {
                return false;
            }

            Type propertyType = cacheHit.Property.PropertyType;
            if (value != null && !TypeHelper.IsCollection(propertyType) && !propertyType.IsAssignableFrom(value.GetType()))
            {
                return false;
            }

            cacheHit.SetValue(_instance, value);
            _changedProperties.Add(name);
            return true;
        }

        private bool TrySetNestedResourceInternal(string name, object deltaNestedResource)
        {
            Debug.Assert(name != null, "Argument name is null");

            if (!(_allProperties.ContainsKey(name) && _updatableProperties.Contains(name)))
            {
                return false;
            }

            if (_deltaNestedResources.ContainsKey(name))
            {
                // Ignore duplicated nested resource.
                return false;
            }

            PropertyAccessor<T> cacheHit = _allProperties[name];
            // Get the Delta<{NestedResourceType}>._instance using Reflection.
            FieldInfo field = deltaNestedResource.GetType().GetField("_instance", BindingFlags.NonPublic | BindingFlags.Instance);
            Contract.Assert(field != null, "field != null");
            cacheHit.SetValue(_instance, field.GetValue(deltaNestedResource));

            // Add the nested resource in the hierarchy.
            // Note: We shouldn't add the structural properties to the <code>_changedProperties</code>, which
            // is used for keeping track of changed non-structural properties at current level.
            _deltaNestedResources[name] = deltaNestedResource;

            return true;
        }
    }
}
