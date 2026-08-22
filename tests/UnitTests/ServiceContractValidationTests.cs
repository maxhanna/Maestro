using System.Text;
using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

public class ServiceContractValidationTests
{
    [Fact]
    public void RejectsIntroducedServiceMethodThatDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_service_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "user-event.service.ts"),
                "export class UserEventService {\n" +
                "  insertUserEvent(userId: number, type: string, message: string, id: number): void {}\n" +
                "}\n", Encoding.UTF8);

            var oldContent = "export class Todo {\n  save() {\n    return true;\n  }\n}\n";
            var newContent = oldContent.Replace(
                "    return true;",
                "    this.userEventService.recordUserEvent('MovieAdded', { movieId: 1 });\n    return true;");

            var error = AgentController.ValidateIntroducedServiceCalls(".ts", oldContent, newContent, root);

            Assert.NotNull(error);
            Assert.Contains("recordUserEvent", error);
            Assert.Contains("no method named", error);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RejectsIntroducedCallWhenServiceClassCannotBeResolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_service_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldContent = "export class Todo {}\n";
            var newContent = "export class Todo {\n  save() {\n    this.userEventService.insertUserEvent(1, 'MovieAdded', 'Added a movie!', 2);\n  }\n}\n";

            var error = AgentController.ValidateIntroducedServiceCalls(".ts", oldContent, newContent, root);

            Assert.NotNull(error);
            Assert.Contains("could not be found", error);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AcceptsExistingServiceMethodWithExactArgumentCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_service_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "user-event.service.ts"),
                "export class UserEventService {\n" +
                "  insertUserEvent(userId: number, type: string, message: string, id: number): void {}\n" +
                "}\n", Encoding.UTF8);

            var oldContent = "export class Todo {\n  save() {\n    return true;\n  }\n}\n";
            var newContent = oldContent.Replace(
                "    return true;",
                "    this.userEventService.insertUserEvent(this.parentRef?.user?.id ?? 0, 'MovieAdded', 'Added a movie!', tmpTodo.id);\n    return true;");

            var error = AgentController.ValidateIntroducedServiceCalls(".ts", oldContent, newContent, root);

            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RejectsExistingServiceMethodWithTooFewArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "weaver_service_contract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "user-event.service.ts"),
                "export class UserEventService {\n" +
                "  insertUserEvent(userId: number, type: string, message: string, id: number): void {}\n" +
                "}\n", Encoding.UTF8);

            var oldContent = "export class Todo {}\n";
            var newContent = "export class Todo {\n  save() {\n    this.userEventService.insertUserEvent('MovieAdded', { movieId: 1 });\n  }\n}\n";

            var error = AgentController.ValidateIntroducedServiceCalls(".ts", oldContent, newContent, root);

            Assert.NotNull(error);
            Assert.Contains("4 parameter", error);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
