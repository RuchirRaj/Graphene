﻿

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Graphene
{
  using Elements;
  using Kinstrife.Core.ReflectionHelpers;

  public static class BindingManager
  {
    /// <summary>
    /// Mapping of all current bindings, keyed by panels
    /// </summary>
    static Dictionary<Plate, List<Binding>> bindings = new Dictionary<Plate, List<Binding>>();

    static Dictionary<Plate, List<Binding>> disposePostUpdate = new Dictionary<Plate, List<Binding>>();

    internal static uint bindingsCount;

#if UNITY_EDITOR
    [UnityEditor.InitializeOnEnterPlayMode]
    public static void InitializeOnEnterPlayMode()
    {
      bindings = new Dictionary<Plate, List<Binding>>();
      disposePostUpdate = new Dictionary<Plate, List<Binding>>();
      bindingsCount = 0;
    }
#endif

    public static void OnUpdate()
    {
      bindingsCount = 0;
      // Update the bindings for active/visible panels
      foreach (var kvp in bindings)
      {
        // Was disposed
        if (!kvp.Key)
          continue;

        // The panel is invisible, or inactive
        if (!kvp.Key.IsActive)
          continue;

        if (kvp.Key.bindingRefreshMode == BindingRefreshMode.None || (kvp.Key.bindingRefreshMode == BindingRefreshMode.ModelChange && !kvp.Key.wasChangedThisFrame))
          continue;
        kvp.Key.wasChangedThisFrame = false;

        foreach (var binding in kvp.Value)
        {
          // Needs to be disposed
          if (binding.scheduleDispose)
          {
            ScheduleDispose(kvp.Key, binding);
            continue;
          }
          // Update the binding
          else
          {
            binding.Update();
            bindingsCount++;
          }
        }
      }

      foreach (var kvp in disposePostUpdate)
        foreach (var binding in kvp.Value)
          Destroy(kvp.Key, binding);

      disposePostUpdate.Clear();
    }

    internal static List<Binding> GetList(Plate panel, Dictionary<Plate, List<Binding>> bindings)
    {
      if (bindings.ContainsKey(panel))
        return bindings[panel];

      List<Binding> list = new List<Binding>();
      bindings.Add(panel, list);
      return list;
    }

    /// <summary>
    /// Creates a continuous binding between a TextElement and a member variable on a context (scope) and panel
    /// </summary>
    /// <param name="el"></param>
    /// <param name="context"></param>
    /// <param name="bindingPath"></param>
    /// <param name="panel"></param>
    public static void TryCreate(TextElement el, ref object context, in ValueWithAttribute<BindAttribute> member, Plate panel)
    {
      // Specifically set to one-time -> cancel binding
      if (member.Attribute.bindingMode.HasValue && member.Attribute.bindingMode.Value == BindingMode.OneTime)
        return;

      CreateBinding<string>(el, ref context, in member, panel);
    }

    /// <summary>
    /// Creates a continuous binding between a BaseField<typeparamref name="TValueType"/> and a member variable on a context (scope) and panel
    /// </summary>
    /// <typeparam name="TValueType"></typeparam>
    /// <param name="el"></param>
    /// <param name="context"></param>
    /// <param name="member"></param>
    /// <param name="panel"></param>
    public static void TryCreate<TValueType>(BaseField<TValueType> el, ref object context, in ValueWithAttribute<BindAttribute> member, Plate panel)
    {
      // Specifically set to one-time
      if (member.Attribute.bindingMode.HasValue && member.Attribute.bindingMode.Value != BindingMode.OneTime)
        return;

      CreateBinding<TValueType>(el, ref context, in member, panel);
    }


    /// <summary>
    /// Creates a continuous binding between a TextElement and a member variable on a context (scope) and panel
    /// </summary>
    /// <param name="el"></param>
    /// <param name="context"></param>
    /// <param name="bindingPath"></param>
    /// <param name="panel"></param>
    public static void TryCreate<TValueType>(BindableElement el, ref object context, in ValueWithAttribute<BindAttribute> member, Plate panel)
    {
      // Specifically set to one-time -> cancel binding
      if (member.Attribute.bindingMode.HasValue && member.Attribute.bindingMode.Value == BindingMode.OneTime)
        return;

      CreateBinding<TValueType>(el, ref context, in member, panel);
    }

    internal static void CreateBinding<TValueType>(BindableElement el, ref object context, in ValueWithAttribute<BindAttribute> member, Plate panel)
    {
      Binding binding = null;
      if (member.MemberInfo is FieldInfo)
        binding = new FieldBinding<TValueType>(el, ref context, in member);
      else if (member.MemberInfo is PropertyInfo)
        binding = new PropertyBinding<TValueType>(el, ref context, in member);

      if (binding != null)
        GetList(panel, bindings).Add(binding);
    }

    public static void ScheduleDispose(Plate panel, Binding binding)
    {
      GetList(panel, disposePostUpdate).Add(binding);
    }

    internal static void Destroy(Plate panel, Binding binding)
    {
      GetList(panel, bindings).Remove(binding);
      binding.Dispose();
      binding = null;
    }

    public static void DisposePlate(Plate plate)
    {
      if (bindings.ContainsKey(plate))
        bindings.Remove(plate);
      if (disposePostUpdate.ContainsKey(plate))
        bindings.Remove(plate);
    }
  }

  /// <summary>
  /// Non-generic base class
  /// </summary>
  public abstract class Binding : IDisposable, IBinding
  {
    public bool scheduleDispose;
    public void Dispose()
    {
      throw new NotImplementedException();
    }

    public abstract void PreUpdate();

    public abstract void Release();

    public abstract void Update();
  }

  public abstract class Binding<T> : Binding
  {
    protected object context;
    [SerializeField] protected T lastValue;
    [SerializeField] protected T newValue;

    protected BindableElement element;
    [SerializeField] BindAttribute attribute;

    // The target field
    protected string memberName;
    protected ExtendedTypeInfo extendedTypeInfo;

    public Binding(BindableElement el, ref object context, in ValueWithAttribute<BindAttribute> member)
    {
      this.element = el;
      this.context = context;
      this.extendedTypeInfo = TypeInfoCache.GetExtendedTypeInfo(context.GetType());

      this.attribute = member.Attribute;
      this.memberName = member.MemberInfo.Name;

      DetermineBindingMode();
    }

    void DetermineBindingMode()
    {
      // Specifically set to not have two-way binding
      if (attribute.bindingMode.HasValue)
      {
        if (attribute.bindingMode == BindingMode.TwoWay)
          RegisterTwoWayValueChangeCallback();
      }
      // No value set - Determine based on control type
      else
      {
        // Can't two-way bind a label
        if (this.element is Label || element is If)
          return;
        else if (this.element is INotifyValueChanged<T>)
          RegisterTwoWayValueChangeCallback();
      }
    }

    public override void PreUpdate()
    {
      throw new NotImplementedException();
    }

    public override void Release()
    {
      throw new NotImplementedException();
    }

    public override void Update()
    {
      // Needs to be disposed because the context ceased to exist
      if (context == null || !IsValidBinding())
      {
        scheduleDispose = true;
        return;
      }

      newValue = GetValueFromMemberInfo();

      // Model changed -> Update view
      if (this.lastValue != null && !this.lastValue.Equals(newValue))
      {
        if (newValue is T && element is BaseField<T> baseField)
          baseField.SetValueWithoutNotify(newValue);
        else if (newValue is string text && element is TextElement textEl)
          textEl.text = text;
        else if (element is IBindableElement<object> bindableEl)
          bindableEl.OnModelChange(newValue);
      }

      lastValue = newValue;
    }

    void RegisterTwoWayValueChangeCallback()
    {
      if (element is INotifyValueChanged<T> notifyChangeEl)
      {
        notifyChangeEl.RegisterValueChangedCallback((evt) => {
          SetValueFromMemberInfo(evt.newValue);
        });
      }
    }

    protected abstract bool IsValidBinding();
    protected abstract T GetValueFromMemberInfo();
    protected abstract void SetValueFromMemberInfo(T value);
  }

  public class PropertyBinding<T> : Binding<T>
  {
    protected PropertyInfo propertyInfo;

    public PropertyBinding(BindableElement el, ref object context, in ValueWithAttribute<BindAttribute> member) : base(el, ref context, in member)
    {
      if (member.MemberInfo is PropertyInfo propInfo)
        this.propertyInfo = propInfo;
      else
      {
        scheduleDispose = true;
        return;
      }

      lastValue = GetValueFromMemberInfo();
    }

    protected override bool IsValidBinding()
    {
      return propertyInfo != null;
    }

    protected override T GetValueFromMemberInfo()
    {
      return (T)extendedTypeInfo.Accessor[context, memberName];
    }

    protected override void SetValueFromMemberInfo(T value)
    {
      extendedTypeInfo.Accessor[context, memberName] = value;
    }
  }

  public class FieldBinding<T> : Binding<T>
  {
    protected FieldInfo fieldInfo;

    public FieldBinding(BindableElement el, ref object context, in ValueWithAttribute<BindAttribute> member) : base(el, ref context, in member)
    {
      if (member.MemberInfo is FieldInfo fieldInfo)
        this.fieldInfo = fieldInfo;
      else
      {
        scheduleDispose = true;
        return;
      }

      lastValue = GetValueFromMemberInfo();
    }

    protected override bool IsValidBinding()
    {
      return memberName != null;
    }
    protected override T GetValueFromMemberInfo()
    {
      return (T)extendedTypeInfo.Accessor[context, memberName];
    }
    protected override void SetValueFromMemberInfo(T value)
    {
      extendedTypeInfo.Accessor[context, memberName] = value;
    }
  }
}