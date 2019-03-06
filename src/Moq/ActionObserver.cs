// Copyright (c) 2007, Clarius Consulting, Manas Technology Solutions, InSTEDD.
// All rights reserved. Licensed under the BSD 3-Clause License; see License.txt.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

using Moq.Expressions.Visitors;
using Moq.Internals;
using Moq.Properties;

namespace Moq
{
	/// <summary>
	///   <see cref="ActionObserver"/> is a kind of <see cref="ExpressionReconstructor"/> that works by
	///   applying a <see cref="Action{T}"/> delegate to a light-weight proxy that records the invocation
	///   happening to it, and auto-generates the same kind of recording proxy for its return value.
	///   That way, a chain of invocation records is generated from which a LINQ expression tree can be
	///   reconstructed.
	/// </summary>
	internal sealed class ActionObserver : ExpressionReconstructor
	{
		public override Expression<Action<T>> ReconstructExpression<T>(Action<T> action)
		{
			using (var ambientObserver = AmbientObserver.Activate())
			{
				// Create the root recording proxy:
				var root = (T)CreateProxy(typeof(T), ambientObserver, out var rootRecorder);

				Exception error = null;
				try
				{
					// Execute the delegate. The root recorder will automatically "mock" return values
					// and so build a chain of recorders, whereby each one records a single invocation
					// in a method chain `o.X.Y.Z`:
					action.Invoke(root);
				}
				catch (Exception ex)
				{
					// Something went wrong. We don't return this error right away. We want to
					// rebuild the expression tree as far as possible for diagnostic purposes.
					error = ex;
				}

				// Start the expression tree with a parameter of type `T`:
				var actionParameters = action.GetMethodInfo().GetParameters();
				var actionParameterName = actionParameters[actionParameters.Length - 1].Name;
				var rootExpression = Expression.Parameter(typeof(T), actionParameterName);
				Expression body = rootExpression;

				// Then step through one recorded invocation at a time:
				for (var recorder = rootRecorder; recorder != null; recorder = recorder.Next)
				{
					var invocation = recorder.Invocation;
					if (invocation != null)
					{
						body = Expression.Call(body, invocation.Method, GetArgumentExpressions(invocation, recorder.Matches.ToArray()));
					}
					else
					{
						// A recorder was set up, but it recorded no invocation. This means
						// that the invocation could not be intercepted:
						throw new ArgumentException(
							string.Format(
								CultureInfo.CurrentCulture,
								Resources.UnsupportedExpressionWithHint,
								$"{actionParameterName} => {body.ToStringFixed()}...",
								Resources.NextMemberNonInterceptable));
					}
				}

				// Now we've either got no error and a completely reconstructed expression, or
				// we have an error and a partially reconstructed expression which we can use for
				// diagnostic purposes:
				if (error == null)
				{
					return Expression.Lambda<Action<T>>(body.Apply(UpgradePropertyAccessorMethods.Rewriter), rootExpression);
				}
				else
				{
					throw new ArgumentException(
						string.Format(
							CultureInfo.CurrentCulture,
							Resources.UnsupportedExpressionWithHint,
							$"{actionParameterName} => {body.ToStringFixed()}...",
							error.Message));
				}
			}

			Expression[] GetArgumentExpressions(Invocation invocation, Match[] matches)
			{
				// First, let's pretend that all arguments are constant values:
				var parameterTypes = invocation.Method.GetParameterTypes();
				var parameterCount = parameterTypes.Count;
				var expressions = new Expression[parameterCount];
				for (int i = 0; i < parameterCount; ++i)
				{
					expressions[i] = Expression.Constant(invocation.Arguments[i], parameterTypes[i]);
				}

				// Now let's override the above constant expressions with argument matchers, if available:
				if (matches.Length > 0)
				{
					int matchIndex = 0;
					for (int argumentIndex = 0; matchIndex < matches.Length && argumentIndex < expressions.Length; ++argumentIndex)
					{
						if (!object.Equals(invocation.Arguments[argumentIndex], matches[matchIndex].RenderExpression.Type.GetDefaultValue()))
						{
							// This parameter has a non-`default` value.  We therefore assume that it isn't
							// a value that was originally produced by a matcher, since they usually return `default`.
							continue;
						}

						if (parameterTypes[argumentIndex].IsAssignableFrom(matches[matchIndex].RenderExpression.Type))
						{
							// We found a potential match. (Matcher type is assignment-compatible to parameter type.)

							if (matchIndex < matches.Length - 1
								&& !(argumentIndex < expressions.Length - 1 || CanDistribute(matchIndex + 1, argumentIndex + 1)))
							{
								// We get here if there are more matchers to distribute,
								// but we either:
								//  * ran out of parameters to distribute over, or
								//  * the remaining matchers can't be distributed over the remaining parameters.
								// In this case, we bail out, which will lead to an exception being thrown.
								break;
							}

							// The remaining matchers can be distributed over the remaining parameters,
							// so we can use up this matcher:
							expressions[argumentIndex] = new MatchExpression(matches[matchIndex]);
							++matchIndex;
						}
					}

					if (matchIndex < matches.Length)
					{
						throw new ArgumentException("Superfluous matchers, possibly due to nested expressions!?");
					}

					bool CanDistribute(int msi, int asi)
					{
						var match = matches[msi];
						var matchType = match.RenderExpression.Type;
						for (int ai = asi; ai < expressions.Length; ++ai)
						{
							if (parameterTypes[ai].IsAssignableFrom(matchType)
								&& CanDistribute(msi + 1, ai + 1))
							{
								return true;
							}
						}
						return false;
					}
				}

				// Finally, add explicit type casts (aka `Convert` nodes) where necessary:
				for (int i = 0; i < expressions.Length; ++i)
				{
					var argument = expressions[i];
					var parameterType = parameterTypes[i];

					// nullable type coercion:
					var argumentValue = invocation.Arguments[i];
					if (Nullable.GetUnderlyingType(parameterType) != null && Nullable.GetUnderlyingType(argument.Type) == null)
					{
						expressions[i] = Expression.Convert(argument, parameterType);
					}

					// boxing of value types (i.e. where a value-typed value is assigned to a reference-typed parameter):
					if (argument.Type.GetTypeInfo().IsValueType && !parameterType.GetTypeInfo().IsValueType)
					{
						expressions[i] = Expression.Convert(argument, parameterType);
					}
				}

				return expressions;
			}
		}

		// Creates a proxy (way more light-weight than a `Mock<T>`!) with an invocation `Recorder` attached to it.
		private static IProxy CreateProxy(Type type, AmbientObserver ambientObserver, out Recorder recorder)
		{
			recorder = new Recorder(ambientObserver);
			return (IProxy)ProxyFactory.Instance.CreateProxy(type, recorder, Type.EmptyTypes, new object[0]);
		}

		// Records an invocation, mocks return values, and builds a chain to the return value's recorder.
		// This record represents the basis for reconstructing an expression tree.
		private sealed class Recorder : IInterceptor
		{
			private readonly AmbientObserver ambientObserver;
			private int creationTimestamp;
			private Invocation invocation;
			private int invocationTimestamp;
			private IProxy returnValue;

			public Recorder(AmbientObserver ambientObserver)
			{
				Debug.Assert(ambientObserver != null);

				this.ambientObserver = ambientObserver;
				this.creationTimestamp = this.ambientObserver.GetNextTimestamp();
			}

			public Invocation Invocation => this.invocation;

			public IEnumerable<Match> Matches
			{
				get
				{
					Debug.Assert(this.invocationTimestamp != default);
					return this.ambientObserver.GetMatchesBetween(this.creationTimestamp, this.invocationTimestamp);
				}
			}

			public Recorder Next => this.returnValue?.Interceptor as Recorder;

			public void Intercept(Invocation invocation)
			{
				var returnType = invocation.Method.ReturnType;

#if DEBUG
				// In theory, each recorder receives exactly one invocation.
				// We put the following guard here for debugging purposes, since your IDE's
				// "Watch" window might cause additional calls that normally shouldn't happen.
				if (this.invocation == null)
#endif
				{
					this.invocation = invocation;
					this.invocationTimestamp = this.ambientObserver.GetNextTimestamp();

					if (returnType == typeof(void))
					{
						this.returnValue = null;
					}
					else if (returnType.IsMockeable())
					{
						this.returnValue = CreateProxy(returnType, this.ambientObserver, out _);
					}
					else
					{
						throw new NotSupportedException(Resources.LastMemberHasNonInterceptableReturnType);
					}
				}

				if (returnType != typeof(void))
				{
					invocation.Return(this.returnValue);
				}
				else
				{
					invocation.Return();
				}
			}
		}
	}
}
