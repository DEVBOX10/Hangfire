﻿// This file is part of Hangfire.
// Copyright © 2013-2014 Sergey Odinokov.
// 
// Hangfire is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as 
// published by the Free Software Foundation, either version 3 
// of the License, or any later version.
// 
// Hangfire is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public 
// License along with Hangfire. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Hangfire.Annotations;
using Hangfire.Server;

namespace Hangfire.Common
{
    /// <summary>
    /// Represents an action that can be marshalled to another process to
    /// be performed.
    /// </summary>
    /// 
    /// <remarks>
    /// <para>The ability to serialize an action is the cornerstone of 
    /// marshalling it outside of a current process boundaries. We are leaving 
    /// behind all the tricky features, e.g. serializing lambdas or so, and 
    /// considering a simple method call information as a such an action,
    /// and using reflection to perform it.</para>
    /// 
    /// <para>Reflection-based method invocation requires an instance of
    /// the <see cref="MethodInfo"/> class, the arguments and an instance of 
    /// the type on which to invoke the method (unless it is static). Since
    /// the same <see cref="MethodInfo"/> instance can be shared across
    /// multiple types (especially when they are defined in interfaces),
    /// we require to explicitly specify a corresponding <see cref="Type"/>
    /// instance to avoid any ambiguities to uniquely determine which type
    /// contains the method to be called.</para>
    /// 
    /// <para>The tuple Type/MethodInfo/Arguments can be easily serialized 
    /// and deserialized back.</para>
    /// </remarks>
    /// 
    /// <seealso cref="IJobPerformanceProcess"/>
    /// 
    /// <threadsafety static="true" instance="false" />
    public class Job
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Job"/> class with the 
        /// given method metadata and no arguments. Its declaring type will be
        /// used to describe a type that contains the method.
        /// </summary>
        /// <param name="method">Method that supposed to be invoked.</param>
        public Job([NotNull] MethodInfo method)
            : this(method, new string[0])
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Job"/> class with the
        /// given method metadata and specified list of serialized arguments.
        /// The type that declares the method will be used to describe a type
        /// that contains the method.
        /// </summary>
        /// <param name="method">Method that supposed to be invoked.</param>
        /// <param name="arguments">Arguments that will be passed to a method invocation.</param>
        public Job([NotNull] MethodInfo method, [NotNull] params string[] arguments)
            : this(method.DeclaringType, method, arguments)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Job"/> class with the
        /// given method metadata, 
        /// a given method data and an empty arguments array.
        /// </summary>
        /// 
        /// <remarks>
        /// Each argument should be serialized into a string using the 
        /// <see cref="JobHelper.ToJson(object)"/> method of the <see cref="JobHelper"/> 
        /// class.
        /// </remarks>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="type"/> argument is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="method"/> argument is null.</exception>
        /// <exception cref="ArgumentException">Method contains unassigned generic type parameters.</exception>
        public Job([NotNull] Type type, [NotNull] MethodInfo method)
            : this(type, method, new string[0])
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Job"/> class with
        /// a given method data and arguments.
        /// </summary>
        /// 
        /// <remarks>
        /// Each argument should be serialized into a string using the 
        /// <see cref="JobHelper.ToJson(object)"/> method of the <see cref="JobHelper"/> 
        /// class.
        /// </remarks>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="type"/> argument is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="method"/> argument is null.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="arguments"/> argument is null.</exception>
        /// <exception cref="ArgumentException">Method contains unassigned generic type parameters.</exception>
        public Job([NotNull] Type type, [NotNull] MethodInfo method, [NotNull] params string[] arguments)
        {
            if (type == null) throw new ArgumentNullException("type");
            if (method == null) throw new ArgumentNullException("method");
            if (arguments == null) throw new ArgumentNullException("arguments");

            if (method.ContainsGenericParameters)
            {
                throw new ArgumentException("Job method can not contain unassigned generic type parameters.", "method");
            }

            Type = type;
            Method = method;
            Arguments = arguments;

            Validate();
        }

        /// <summary>
        /// Gets the metadata of a type that contains a method that supposed 
        /// to be invoked during the performance.
        /// </summary>
        [NotNull]
        public Type Type { get; private set; }

        /// <summary>
        /// Gets the metadata of a method that supposed to be invoked during
        /// the performance.
        /// </summary>
        [NotNull]
        public MethodInfo Method { get; private set; }

        /// <summary>
        /// Gets an array of arguments that will be passed to a method 
        /// invocation during the performance.
        /// </summary>
        [NotNull]
        public string[] Arguments { get; private set; }

        [Obsolete("This method is deprecated. Please use `CoreJobPerformanceProcess` or `JobPerformanceProcess` classes instead. Will be removed in 2.0.0.")]
        public object Perform(JobActivator activator, IJobCancellationToken cancellationToken)
        {
            if (activator == null) throw new ArgumentNullException("activator");
            if (cancellationToken == null) throw new ArgumentNullException("cancellationToken");

            object instance = null;

            object result;
            try
            {
                if (!Method.IsStatic)
                {
                    instance = Activate(activator);
                }

                var deserializedArguments = DeserializeArguments(cancellationToken);
                result = InvokeMethod(instance, deserializedArguments);
            }
            finally
            {
                Dispose(instance);
            }

            return result;
        }

        internal IEnumerable<JobFilterAttribute> GetTypeFilterAttributes(bool useCache)
        {
            return useCache
                ? ReflectedAttributeCache.GetTypeFilterAttributes(Type)
                : GetFilterAttributes(Type);
        }

        internal IEnumerable<JobFilterAttribute> GetMethodFilterAttributes(bool useCache)
        {
            return useCache
                ? ReflectedAttributeCache.GetMethodFilterAttributes(Method)
                : GetFilterAttributes(Method);
        }

        private static IEnumerable<JobFilterAttribute> GetFilterAttributes(MemberInfo memberInfo)
        {
            return memberInfo
                .GetCustomAttributes(typeof(JobFilterAttribute), inherit: true)
                .Cast<JobFilterAttribute>();
        }

        /// <summary>
        /// Creates a new instance of the <see cref="Job"/> class on a 
        /// basis of the given static method call expression.
        /// </summary>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="methodCall"/> argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="methodCall"/> expression body does not contain <see cref="MethodCallExpression"/>.</exception>
        public static Job FromExpression([InstantHandle] Expression<Action> methodCall)
        {
            if (methodCall == null) throw new ArgumentNullException("methodCall");

            var callExpression = methodCall.Body as MethodCallExpression;
            if (callExpression == null)
            {
                throw new NotSupportedException("Expression body should be of type `MethodCallExpression`");
            }

            Type type;

            if (callExpression.Object != null)
            {
                var objectValue = GetExpressionValue(callExpression.Object);
                if (objectValue == null)
                {
                    throw new InvalidOperationException("Expression object should not be null.");
                }

                type = objectValue.GetType();
            }
            else
            {
                type = callExpression.Method.DeclaringType;
            }

            // Static methods can not be overridden in the derived classes, 
            // so we can take the method's declaring type.
            return new Job(
                type,
                callExpression.Method,
                GetArguments(callExpression));
        }

        /// <summary>
        /// Creates a new instance of the <see cref="Job"/> class on a 
        /// basis of the given instance method call expression.
        /// </summary>
        /// 
        /// <exception cref="ArgumentNullException"><paramref name="methodCall"/> argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="methodCall"/> expression body does not contain <see cref="MethodCallExpression"/>.</exception>
        public static Job FromExpression<T>([InstantHandle] Expression<Action<T>> methodCall)
        {
            if (methodCall == null) throw new ArgumentNullException("methodCall");

            var callExpression = methodCall.Body as MethodCallExpression;
            if (callExpression == null)
            {
                throw new NotSupportedException("Expression body should be of type `MethodCallExpression`");
            }

            return new Job(
                typeof(T),
                callExpression.Method,
                GetArguments(callExpression));
        }

        private void Validate()
        {
            if (Method.DeclaringType == null)
            {
                throw new NotSupportedException("Global methods are not supported. Use class methods instead.");
            }

            if (!Method.DeclaringType.IsAssignableFrom(Type))
            {
                throw new ArgumentException(String.Format(
                    "The type `{0}` must be derived from the `{1}` type.",
                    Method.DeclaringType,
                    Type));
            }

            if (!Method.IsPublic)
            {
                throw new NotSupportedException("Only public methods can be invoked in the background.");
            }

            if (typeof(Task).IsAssignableFrom(Method.ReturnType))
            {
                throw new NotSupportedException("Async methods are not supported. Please make them synchronous before using them in background.");
            }

            var parameters = Method.GetParameters();

            if (parameters.Length != Arguments.Length)
            {
                throw new ArgumentException("Argument count must be equal to method parameter count.");
            }

            foreach (var parameter in parameters)
            {
                // There is no guarantee that specified method will be invoked
                // in the same process. Therefore, output parameters and parameters
                // passed by reference are not supported.

                if (parameter.IsOut)
                {
                    throw new NotSupportedException(
                        "Output parameters are not supported: there is no guarantee that specified method will be invoked inside the same process.");
                }

                if (parameter.ParameterType.IsByRef)
                {
                    throw new NotSupportedException(
                        "Parameters, passed by reference, are not supported: there is no guarantee that specified method will be invoked inside the same process.");
                }
            }
        }

        private object Activate(JobActivator activator)
        {
            try
            {
                var instance = activator.ActivateJob(Type);

                if (instance == null)
                {
                    throw new InvalidOperationException(
                        String.Format("JobActivator returned NULL instance of the '{0}' type.", Type));
                }

                return instance;
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "An exception occurred during job activation.",
                    ex);
            }
        }

        private object[] DeserializeArguments(IJobCancellationToken cancellationToken)
        {
            try
            {
                var parameters = Method.GetParameters();
                var result = new List<object>(Arguments.Length);

                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var argument = Arguments[i];

                    object value;

                    if (typeof(IJobCancellationToken).IsAssignableFrom(parameter.ParameterType))
                    {
                        value = cancellationToken;
                    }
                    else
                    {
                        try
                        {
                            value = argument != null
                                ? JobHelper.FromJson(argument, parameter.ParameterType)
                                : null;
                        }
                        catch (Exception)
                        {
                            if (parameter.ParameterType == typeof(object))
                            {
                                // Special case for handling object types, because string can not
                                // be converted to object type.
                                value = argument;
                            }
                            else
                            {
                                var converter = TypeDescriptor.GetConverter(parameter.ParameterType);
                                value = converter.ConvertFromInvariantString(argument);
                            }
                        }
                    }

                    result.Add(value);
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "An exception occurred during arguments deserialization.",
                    ex);
            }
        }

        private object InvokeMethod(object instance, object[] deserializedArguments)
        {
            try
            {
                return Method.Invoke(instance, deserializedArguments);
            }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException is OperationCanceledException)
                {
                    // `OperationCanceledException` and its descendants are used
                    // to notify a worker that job performance was canceled,
                    // so we should not wrap this exception and throw it as-is.
                    throw ex.InnerException;
                }

                throw new JobPerformanceException(
                    "An exception occurred during performance of the job.",
                    ex.InnerException);
            }
        }

        private static void Dispose(object instance)
        {
            try
            {
                var disposable = instance as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                throw new JobPerformanceException(
                    "Job has been performed, but an exception occurred during disposal.",
                    ex);
            }
        }

        private static string[] GetArguments(MethodCallExpression callExpression)
        {
            Debug.Assert(callExpression != null);

            var arguments = callExpression.Arguments.Select(GetExpressionValue).ToArray();

            var serializedArguments = new List<string>(arguments.Length);
            foreach (var argument in arguments)
            {
                string value = null;

                if (argument != null)
                {
                    if (argument is DateTime)
                    {
                        value = ((DateTime)argument).ToString("o", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        value = JobHelper.ToJson(argument);
                    }
                }

                // Logic, related to optional parameters and their default values, 
                // can be skipped, because it is impossible to omit them in 
                // lambda-expressions (leads to a compile-time error).

                serializedArguments.Add(value);
            }

            return serializedArguments.ToArray();
        }

        private static object GetExpressionValue(Expression expression)
        {
            Debug.Assert(expression != null);

            var constantExpression = expression as ConstantExpression;

            if (constantExpression != null)
            {
                return constantExpression.Value;
            }

            return CachedExpressionCompiler.Evaluate(expression);
        }

        public override string ToString()
        {
            return String.Format("{0}.{1}", Type.ToGenericTypeString(), Method.Name);
        }
    }
}
