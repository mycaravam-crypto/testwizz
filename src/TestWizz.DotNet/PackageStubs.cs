namespace TestWizz.DotNet;

/// <summary>
/// Minimal, permissive declarations of the test-package APIs that generated tests use most. They are added only to the
/// in-memory compilation used for validation, so that calls into the code under test (including inside lambdas passed
/// to <c>Assert.Throws</c> or mock setups) are type-checked. Diagnostics are only reported for repository types, so a
/// stub that is looser than the real package never produces findings of its own.
/// </summary>
internal static class PackageStubs
{
    public const string Xunit = """
        namespace Xunit
        {
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            [AttributeUsage(AttributeTargets.Method)] public class FactAttribute : Attribute { public string? Skip { get; set; } public string? DisplayName { get; set; } public int Timeout { get; set; } }
            [AttributeUsage(AttributeTargets.Method)] public class TheoryAttribute : FactAttribute { }
            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public class InlineDataAttribute : Attribute { public InlineDataAttribute(params object?[] data) { } }
            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public class MemberDataAttribute : Attribute { public MemberDataAttribute(string memberName, params object?[] parameters) { } public Type? MemberType { get; set; } }
            [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public class ClassDataAttribute : Attribute { public ClassDataAttribute(Type type) { } }
            [AttributeUsage(AttributeTargets.All, AllowMultiple = true)] public class TraitAttribute : Attribute { public TraitAttribute(string name, string value) { } }
            public class TheoryData<T> : IEnumerable<object?[]> { public void Add(T value) { } public IEnumerator<object?[]> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => null!; }
            public class TheoryData<T1, T2> : IEnumerable<object?[]> { public void Add(T1 a, T2 b) { } public IEnumerator<object?[]> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => null!; }
            public class TheoryData<T1, T2, T3> : IEnumerable<object?[]> { public void Add(T1 a, T2 b, T3 c) { } public IEnumerator<object?[]> GetEnumerator() => null!; IEnumerator IEnumerable.GetEnumerator() => null!; }
            public interface IClassFixture<T> { }
            public interface IAsyncLifetime { }
            public interface ITestOutputHelper { void WriteLine(string message); }

            public static class Record
            {
                public static Exception? Exception(Action action) => null;
                public static Exception? Exception(Func<object?> func) => null;
                public static Task<Exception?> ExceptionAsync(Func<Task> func) => null!;
            }

            public static class Assert
            {
                public static void Equal<T>(T expected, T actual) { }
                public static void Equal<T>(IEnumerable<T>? expected, IEnumerable<T>? actual) { }
                public static void Equal(double expected, double actual, int precision) { }
                public static void Equal(decimal expected, decimal actual, int precision) { }
                public static void Equal(string? expected, string? actual, bool ignoreCase = false, bool ignoreLineEndingDifferences = false, bool ignoreWhiteSpaceDifferences = false) { }
                public static void NotEqual<T>(T expected, T actual) { }
                public static void Equivalent(object? expected, object? actual, bool strict = false) { }
                public static void True(bool? condition, string? userMessage = null) { }
                public static void False(bool? condition, string? userMessage = null) { }
                public static void Null(object? value) { }
                public static void NotNull(object? value) { }
                public static void Same(object? expected, object? actual) { }
                public static void NotSame(object? expected, object? actual) { }
                public static void Empty(IEnumerable collection) { }
                public static void NotEmpty(IEnumerable collection) { }
                public static T Single<T>(IEnumerable<T> collection) => default!;
                public static T Single<T>(IEnumerable<T> collection, Predicate<T> predicate) => default!;
                public static void Contains<T>(T expected, IEnumerable<T> collection) { }
                public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter) { }
                public static void Contains(string expectedSubstring, string? actualString, StringComparison comparisonType = StringComparison.CurrentCulture) { }
                public static void DoesNotContain<T>(T expected, IEnumerable<T> collection) { }
                public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter) { }
                public static void DoesNotContain(string expectedSubstring, string? actualString, StringComparison comparisonType = StringComparison.CurrentCulture) { }
                public static void StartsWith(string? expectedStartString, string? actualString, StringComparison comparisonType = StringComparison.CurrentCulture) { }
                public static void EndsWith(string? expectedEndString, string? actualString, StringComparison comparisonType = StringComparison.CurrentCulture) { }
                public static void Matches(string expectedRegexPattern, string? actualString) { }
                public static void All<T>(IEnumerable<T> collection, Action<T> action) { }
                public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors) { }
                public static void InRange<T>(T actual, T low, T high) where T : IComparable { }
                public static void NotInRange<T>(T actual, T low, T high) where T : IComparable { }
                public static T IsType<T>(object? @object) => default!;
                public static T IsAssignableFrom<T>(object? @object) => default!;
                public static void IsNotType<T>(object? @object) { }
                public static T Throws<T>(Action testCode) where T : Exception => default!;
                public static T Throws<T>(Func<object?> testCode) where T : Exception => default!;
                public static T Throws<T>(string? paramName, Action testCode) where T : ArgumentException => default!;
                public static T Throws<T>(string? paramName, Func<object?> testCode) where T : ArgumentException => default!;
                public static Exception ThrowsAny<T>(Action testCode) where T : Exception => default!;
                public static Task<T> ThrowsAsync<T>(Func<Task> testCode) where T : Exception => default!;
                public static Task<T> ThrowsAsync<T>(string? paramName, Func<Task> testCode) where T : ArgumentException => default!;
                public static Task<T> ThrowsAnyAsync<T>(Func<Task> testCode) where T : Exception => default!;
                public static void Multiple(params Action[] checks) { }
                public static void Fail(string message) { }
            }
        }
        """;

    public const string NSubstitute = """
        namespace NSubstitute
        {
            using System;
            using System.Threading.Tasks;
            using NSubstitute.Core;

            public static class Substitute
            {
                public static T For<T>(params object[] constructorArguments) where T : class => default!;
                public static T ForPartsOf<T>(params object[] constructorArguments) where T : class => default!;
            }

            public static class Arg
            {
                public static T Any<T>() => default!;
                public static T Is<T>(T value) => default!;
                public static T Is<T>(System.Linq.Expressions.Expression<Predicate<T>> predicate) => default!;
                public static T Do<T>(Action<T> useArgument) => default!;
            }

            public static class SubstituteExtensions
            {
                public static T Received<T>(this T substitute, int requiredNumberOfCalls = 1) where T : class => substitute;
                public static T DidNotReceive<T>(this T substitute) where T : class => substitute;
                public static T ReceivedWithAnyArgs<T>(this T substitute, int requiredNumberOfCalls = 1) where T : class => substitute;
                public static T DidNotReceiveWithAnyArgs<T>(this T substitute) where T : class => substitute;
                public static void ClearReceivedCalls<T>(this T substitute) where T : class { }
                public static ConfiguredCall Returns<T>(this T value, T returnThis, params T[] returnThese) => null!;
                public static ConfiguredCall Returns<T>(this T value, Func<CallInfo, T> returnThis, params Func<CallInfo, T>[] returnThese) => null!;
                public static ConfiguredCall Returns<T>(this Task<T> value, T returnThis, params T[] returnThese) => null!;
                public static ConfiguredCall ReturnsForAnyArgs<T>(this T value, T returnThis, params T[] returnThese) => null!;
                public static ConfiguredCall ReturnsNull<T>(this T value) where T : class => null!;
                public static WhenCalled<T> When<T>(this T substitute, Action<T> substituteCall) where T : class => null!;
                public static WhenCalled<T> When<T>(this T substitute, Func<T, Task> substituteCall) where T : class => null!;
            }
        }

        namespace NSubstitute.Core
        {
            using System;
            public class CallInfo { public object[] Args() => null!; public T Arg<T>() => default!; public object this[int index] => null!; }
            public class ConfiguredCall { public ConfiguredCall AndDoes(Action<CallInfo> action) => this; }
            public class WhenCalled<T> { public void Do(Action<CallInfo> callback) { } public void Throw(Exception exception) { } public void Throw<TException>() where TException : Exception, new() { } }
        }

        namespace NSubstitute.ExceptionExtensions
        {
            using System;
            public static class ExceptionExtensions
            {
                public static NSubstitute.Core.ConfiguredCall Throws<T>(this T value, Exception ex) => null!;
                public static NSubstitute.Core.ConfiguredCall Throws<TException>(this object value) where TException : Exception, new() => null!;
                public static NSubstitute.Core.ConfiguredCall ThrowsAsync<T>(this T value, Exception ex) => null!;
                public static NSubstitute.Core.ConfiguredCall ThrowsAsync<TException>(this object value) where TException : Exception, new() => null!;
                public static NSubstitute.Core.ConfiguredCall ThrowsForAnyArgs<T>(this T value, Exception ex) => null!;
            }
        }
        """;

    public const string Moq = """
        namespace Moq
        {
            using System;
            using System.Linq.Expressions;
            using System.Threading.Tasks;

            public enum MockBehavior { Default, Strict, Loose }
            public class Mock<T> where T : class
            {
                public Mock() { }
                public Mock(MockBehavior behavior) { }
                public Mock(params object[] args) { }
                public T Object => default!;
                public ISetup<T, TResult> Setup<TResult>(Expression<Func<T, TResult>> expression) => null!;
                public ISetup<T> Setup(Expression<Action<T>> expression) => null!;
                public ISetupGetter<T, TProperty> SetupGet<TProperty>(Expression<Func<T, TProperty>> expression) => null!;
                public ISetupSequentialResult<TResult> SetupSequence<TResult>(Expression<Func<T, TResult>> expression) => null!;
                public void Verify(Expression<Action<T>> expression) { }
                public void Verify(Expression<Action<T>> expression, Times times) { }
                public void Verify(Expression<Action<T>> expression, Func<Times> times) { }
                public void Verify<TResult>(Expression<Func<T, TResult>> expression) { }
                public void Verify<TResult>(Expression<Func<T, TResult>> expression, Times times) { }
                public void Verify<TResult>(Expression<Func<T, TResult>> expression, Func<Times> times) { }
                public void VerifyGet<TProperty>(Expression<Func<T, TProperty>> expression) { }
                public void VerifyNoOtherCalls() { }
                public void VerifyAll() { }
            }

            public interface ISetup<T> where T : class { ISetup<T> Throws(Exception exception); ISetup<T> Throws<TException>() where TException : Exception, new(); ISetup<T> Callback(Delegate action); void Verifiable(); }
            public interface ISetup<T, TResult> where T : class
            {
                ISetup<T, TResult> Returns(TResult value);
                ISetup<T, TResult> Returns(Delegate valueFunction);
                ISetup<T, TResult> Throws(Exception exception);
                ISetup<T, TResult> Throws<TException>() where TException : Exception, new();
                ISetup<T, TResult> Callback(Delegate action);
                void Verifiable();
            }
            public interface ISetupGetter<T, TProperty> where T : class { ISetupGetter<T, TProperty> Returns(TProperty value); }
            public interface ISetupSequentialResult<TResult> { ISetupSequentialResult<TResult> Returns(TResult value); ISetupSequentialResult<TResult> Throws(Exception exception); }

            public static class MoqExtensions
            {
                public static ISetup<T, Task<TResult>> ReturnsAsync<T, TResult>(this ISetup<T, Task<TResult>> setup, TResult value) where T : class => setup;
                public static ISetup<T, Task<TResult>> ReturnsAsync<T, TResult>(this ISetup<T, Task<TResult>> setup, Func<TResult> valueFunction) where T : class => setup;
                public static ISetup<T, Task> ThrowsAsync<T>(this ISetup<T, Task> setup, Exception exception) where T : class => setup;
                public static ISetup<T, Task<TResult>> ThrowsAsync<T, TResult>(this ISetup<T, Task<TResult>> setup, Exception exception) where T : class => setup;
            }

            public static class It
            {
                public static T IsAny<T>() => default!;
                public static T Is<T>(Expression<Func<T, bool>> match) => default!;
                public static T IsIn<T>(params T[] items) => default!;
                public static T IsNotNull<T>() => default!;
            }

            public struct Times
            {
                public static Times Once() => default;
                public static Times Never() => default;
                public static Times Exactly(int callCount) => default;
                public static Times AtLeastOnce() => default;
                public static Times AtLeast(int callCount) => default;
                public static Times AtMost(int callCount) => default;
            }
        }
        """;

    public static IEnumerable<string> For(string? mockingLibrary)
    {
        yield return Xunit;
        if (mockingLibrary == "NSubstitute")
        {
            yield return NSubstitute;
        }
        else if (mockingLibrary == "Moq")
        {
            yield return Moq;
        }
    }
}
