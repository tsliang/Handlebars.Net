﻿using System;
using System.Collections;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.IO;
using System.Dynamic;
using HandlebarsDotNet;
using HandlebarsDotNet.Compiler;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Handlebars.Compiler
{
    internal class PathBinder : HandlebarsExpressionVisitor
    {
        public static Expression Bind(Expression expr, CompilationContext context)
        {
            return new PathBinder(context).Visit(expr);
        }

        private PathBinder(CompilationContext context)
            : base(context)
        {
        }

        protected override Expression VisitBlock(BlockExpression node)
        {
            return Expression.Block(
                node.Variables,
                node.Expressions.Select(expr => Visit(expr)));
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            return Expression.MakeUnary(
                node.NodeType,
                Visit(node.Operand),
                node.Type);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            return Expression.Call(
                Visit(node.Object),
                node.Method,
                node.Arguments.Select(n => Visit(n)));
        }

        protected override Expression VisitConditional(ConditionalExpression node)
        {
            return Expression.Condition(
                Visit(node.Test),
                Visit(node.IfTrue),
                Visit(node.IfFalse));
        }

        protected override Expression VisitStatementExpression(StatementExpression sex)
        {
            if (sex.Body is PathExpression)
            {
                var writeMethod = typeof(TextWriter).GetMethod("Write", new [] { typeof(object) });
                return Expression.Call(
                    Expression.Property(
                        CompilationContext.BindingContext,
                        "TextWriter"),
                    writeMethod,
                    new[] { Visit(sex.Body) });
            }
            else
            {
                return Visit(sex.Body);
            }
        }

        protected override Expression VisitPathExpression(PathExpression pex)
        {
            return Expression.Call(
                Expression.Constant(this),
                new Func<BindingContext, string, object>(ResolvePath).Method,
                CompilationContext.BindingContext,
                Expression.Constant(pex.Path));
        }

        protected override Expression VisitHelperExpression(HelperExpression hex)
        {
            return HandlebarsExpression.Helper(
                hex.HelperName,
                hex.Arguments.Select(arg => Visit(arg)));
        }

        //TODO: make path resolution logic smarter
        private object ResolvePath(BindingContext context, string path)
        {
            var instance = context.Value;
            foreach (var segment in path.Split ('/'))
            {
                if (segment == "..")
                {
                    context = context.ParentContext;
                    if (context == null)
                    {
                        throw new HandlebarsCompilerException("Path expression tried to reference parent of root");
                    }
                    instance = context.Value;
                }
                else if (segment == "this")
                {
                    continue;
                }
                else
                {
                    foreach (var memberName in segment.Split('.'))
                    {
                        try
                        {
                            instance = this.ResolveValue(context, instance, memberName);
                        }
                        catch (Exception)
                        {
                            instance = new UndefinedBindingResult();
                            break;
                        }
                    }
                }
            }
            return instance;
        }

        private object ResolveValue(BindingContext context, object instance, string segment)
        {
            if (segment.StartsWith("@"))
            {
                var contextValue = context.GetContextVariable(segment.Substring(1));
                if (contextValue == null)
                {
                    throw new HandlebarsRuntimeException("Couldn't bind to context variable");
                }
                return contextValue;
            }
            else
            {
                return AccessMember(instance, segment);
            }
        }

        private object AccessMember(object instance, string memberName)
        {
            var resolvedMemberName = this.ResolveMemberName(memberName);
            //crude handling for dynamic objects that don't have metadata
            if (instance is IDynamicMetaObjectProvider)
            {
                try
                {
                    return GetProperty(instance, resolvedMemberName);
                }
                catch (Exception ex)
                {
                    throw new HandlebarsRuntimeException("Could not resolve dynamic member name", ex);
                }
            }

            var instanceType = instance.GetType();
            if (instance is IDictionary)
            {
                return ((IDictionary)instance)[resolvedMemberName];
            }
            if (instanceType.GetInterfaces()
                .Where(i => i.IsGenericType)
                .Any(i => i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
               return instanceType.GetMethod("get_Item").Invoke(instance, new object[] { resolvedMemberName });
            }
            var members = instanceType.GetMember(resolvedMemberName);
            if (members.Length == 0)
            {
                throw new InvalidOperationException("Template referenced property name that does not exist.");
            }
            if (members[0].MemberType == MemberTypes.Property)
            {
                return ((PropertyInfo)members[0]).GetValue(instance, null);
            }
            if (members[0].MemberType == MemberTypes.Field)
            {
                return ((FieldInfo)members[0]).GetValue(instance);
            }
            var enumerable = instance as IEnumerable<object>;
            if (enumerable != null) {
                var index = 0;
                var indexRegex = new Regex(@"^\[?(\d+)\]?$");
                var match = indexRegex.Match(memberName);
                if (!match.Success || match.Groups.Count < 2 || !int.TryParse(match.Groups[1].Value, out index)) {
                    throw new HandlebarsRuntimeException("Invalid array index in path.");
                }
                return enumerable.ElementAt(index);
            }
            throw new InvalidOperationException("Requested member was not a field or property");
        }

        private static object GetProperty(object target, string name)
        {
            var site = System.Runtime.CompilerServices.CallSite<Func<System.Runtime.CompilerServices.CallSite, object, object>>.Create(Microsoft.CSharp.RuntimeBinder.Binder.GetMember(0, name, target.GetType(), new[]{ Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo.Create(0, null) }));
            return site.Target(site, target);
        }

        private string ResolveMemberName(string memberName)
        {
            var resolver = this.CompilationContext.Configuration.ExpressionNameResolver;
            return resolver != null ? resolver.ResolveExpressionName(memberName) : memberName;
        }
    }
}

