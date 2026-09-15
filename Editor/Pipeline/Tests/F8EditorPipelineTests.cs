using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

namespace F8Framework.Core.Editor.Tests
{
    public sealed class F8EditorPipelineTests
    {
        [SetUp]
        public void SetUp()
        {
            F8EditorPipeline.CancelPending();
            CompletedTestStep.ExecutionCount = 0;
            ErrorLogTestStep.ShouldFail = true;
        }

        [TearDown]
        public void TearDown()
        {
            F8EditorPipeline.CancelPending();
        }

        [Test]
        public void BuilderOrdersByOrderThenInsertionSequence()
        {
            F8EditorPipelineBuilder builder = new F8EditorPipelineBuilder("Order Test")
                .Add("third", 30)
                .Add("first-a", 10)
                .Add("first-b", 10)
                .Add("second", 20);

            string[] orderedIds = builder.GetOrderedSteps().Select(step => step.Id).ToArray();

            CollectionAssert.AreEqual(
                new[] { "first-a", "first-b", "second", "third" },
                orderedIds);
        }

        [Test]
        public void CompletedPipelineRunsAndClearsPersistentState()
        {
            F8EditorPipelineBuilder builder = new F8EditorPipelineBuilder("Completion Test")
                .Add(CompletedTestStep.StepId, 0, "Complete");

            F8EditorPipeline.Start(builder);

            Assert.AreEqual(1, CompletedTestStep.ExecutionCount);
            Assert.IsFalse(F8EditorPipeline.HasPendingPipeline);
        }

        [Test]
        public void BuildWithoutExtensionsContainsOnlyRequestedCoreSteps()
        {
            F8BuildRequest request = new F8BuildRequest
            {
                DisplayName = "Core Only",
                IncludeExtensions = false,
                GenerateHotUpdateDll = true,
                BuildAssetBundles = true,
            };

            IReadOnlyList<F8EditorPipelineStepDefinition> steps =
                F8BuildPipeline.CreateBuilder(request).GetOrderedSteps();

            Assert.AreEqual(2, steps.Count);
            Assert.IsFalse(steps.Any(step => step.Id.StartsWith("f8.excel.")));
            Assert.Less(steps[0].Order, steps[1].Order);
        }

        [Test]
        public void PlayerAndUpdateBuildCannotBeRequestedTogether()
        {
            F8BuildRequest request = new F8BuildRequest
            {
                BuildPlayer = true,
                BuildUpdate = true,
            };

            Assert.Throws<System.InvalidOperationException>(() =>
                F8BuildPipeline.CreateBuilder(request));
        }

        [Test]
        public void RequiredCommandLineValueRejectsMissingValue()
        {
            string[] arguments = { "Platform-", "BuildPath-", "C:/Build" };

            Assert.Throws<System.ArgumentException>(() =>
                F8EditorCommandLine.GetRequiredValue(arguments, "Platform-"));
        }

        [Test]
        public void CorruptStateCanStillBeDetectedAndCancelled()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, "{ invalid json");

            Assert.IsTrue(F8EditorPipeline.HasPendingPipeline);
            Assert.AreEqual("无效的流水线状态", F8EditorPipeline.PendingPipelineName);
            Assert.IsTrue(F8EditorPipeline.CancelPending());
            Assert.IsFalse(F8EditorPipeline.HasPendingPipeline);
        }

        [Test]
        public void TemporaryStateCanStillBeDetectedAndCancelled()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StateTempPath, "{ invalid json");

            Assert.IsTrue(F8EditorPipeline.HasPendingPipeline);
            Assert.AreEqual("无效的流水线状态", F8EditorPipeline.PendingPipelineName);
            Assert.IsTrue(F8EditorPipeline.CancelPending());
            Assert.IsFalse(File.Exists(StateTempPath));
        }

        [Test]
        public void ValidTemporaryStateIsUsedWhenMainStateIsCorrupt()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, "{ invalid json");
            File.WriteAllText(
                StateTempPath,
                "{\"Version\":1,\"PipelineId\":\"test\"," +
                "\"DisplayName\":\"Temporary State\",\"Steps\":[]," +
                "\"NextStepIndex\":0,\"Failed\":true}");

            Assert.AreEqual("Temporary State", F8EditorPipeline.PendingPipelineName);
            Assert.IsTrue(F8EditorPipeline.CancelPending());
        }

        [TestCase(LogType.Error)]
        [TestCase(LogType.Assert)]
        public void ErrorLogFailsGuardEvenWhenActionReturnsNormally(LogType type)
        {
            LogAssert.Expect(type, "Failed to compile player scripts");
            BuildFailedException exception = Assert.Throws<BuildFailedException>(() =>
                F8BuildGuard.Run("Compile DLL", () =>
                {
                    Debug.unityLogger.Log(type, "Failed to compile player scripts");
                    return 123;
                }));

            StringAssert.Contains("Compile DLL", exception.Message);
            StringAssert.Contains("Failed to compile player scripts", exception.Message);
            Assert.AreEqual(456, F8BuildGuard.Run("Next operation", () => 456));
        }

        [Test]
        public void GuardIgnoresWarningsAndEarlierErrors()
        {
            LogAssert.Expect(LogType.Error, "Earlier error");
            Debug.LogError("Earlier error");
            LogAssert.Expect(LogType.Warning, "Warning only");
            Assert.DoesNotThrow(() => F8BuildGuard.Run("Warning test", () =>
                Debug.LogWarning("Warning only")));
        }

        [Test]
        public void GuardPreservesThrownExceptionAndCanRunAgain()
        {
            var expected = new IOException("Copy failed");
            Assert.AreSame(expected, Assert.Throws<IOException>(() =>
                F8BuildGuard.Run("Copy", (System.Action)(() => { throw expected; }))));
            Assert.AreEqual(1, F8BuildGuard.Run("Retry", () => 1));
        }

        [Test]
        public void LoggedExceptionFailsGuard()
        {
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: Compile failed"));
            Assert.Throws<BuildFailedException>(() => F8BuildGuard.Run("Compile", () =>
                Debug.LogException(new System.InvalidOperationException("Compile failed"))));
        }

        [Test]
        public void ErrorLogStopsPipelinePersistsFailedStepAndAllowsRetry()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath,
                "{\"Version\":1,\"PipelineId\":\"test-error\",\"DisplayName\":\"Compile Test\"," +
                "\"Steps\":[{\"Id\":\"" + ErrorLogTestStep.StepId + "\",\"DisplayName\":\"Compile DLL\"}," +
                "{\"Id\":\"" + CompletedTestStep.StepId + "\",\"DisplayName\":\"Publish\"}]," +
                "\"NextStepIndex\":0}");

            LogAssert.Expect(LogType.Error, "Failed to compile player scripts");
            LogAssert.Expect(LogType.Exception, new Regex("BuildFailedException:"));
            Assert.Throws<BuildFailedException>(() => F8EditorPipeline.ResumePending(true));

            Assert.AreEqual(0, CompletedTestStep.ExecutionCount, "失败后不能发布热更新包");
            string failedState = File.ReadAllText(StatePath);
            StringAssert.Contains("\"Failed\": true", failedState);
            StringAssert.Contains("\"NextStepIndex\": 0", failedState);
            StringAssert.Contains("Failed to compile player scripts", failedState);

            ErrorLogTestStep.ShouldFail = false;
            Assert.IsTrue(F8EditorPipeline.RetryFailed());
            Assert.AreEqual(1, CompletedTestStep.ExecutionCount);
            Assert.IsFalse(F8EditorPipeline.HasPendingPipeline);
        }

        public sealed class ErrorLogTestStep : IF8EditorPipelineStep
        {
            public const string StepId = "f8.tests.error-log";
            public static bool ShouldFail = true;
            public string Id => StepId;

            public F8EditorPipelineStepResult Execute(F8EditorPipelineContext context)
            {
                if (ShouldFail) Debug.LogError("Failed to compile player scripts");
                return F8EditorPipelineStepResult.Completed;
            }
        }

        private static string StatePath => Path.GetFullPath(Path.Combine(
            Application.dataPath,
            "../Library/F8EditorPipeline/state.json"));

        private static string StateTempPath => StatePath + ".tmp";

        public sealed class CompletedTestStep : IF8EditorPipelineStep
        {
            public const string StepId = "f8.tests.completed";
            public static int ExecutionCount;

            public string Id => StepId;

            public F8EditorPipelineStepResult Execute(F8EditorPipelineContext context)
            {
                ExecutionCount++;
                return F8EditorPipelineStepResult.Completed;
            }
        }
    }
}
