using System;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Json.Llm.Tests;

public sealed class ReusedSchemaTests
{
    [Fact]
    public async Task A_schema_that_reuses_itself_again_and_again_is_refused_quickly_by_a_provider_profile()
    {
        ISchema schema = Z.Object().Property("leaf", Z.String());

        for (int index = 0; index < 30; index++)
        {
            schema = Z.Object().Property("a", schema).Property("b", schema);
        }

        ObjectSchema root = (ObjectSchema)schema;
        Task<InvalidOperationException> task = Task.Run(() => Assert.Throws<InvalidOperationException>(() => root.Emit(LlmProfiles.OpenAIStrict)), TestContext.Current.CancellationToken);
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));

        Assert.Same(task, finished);
        await task;
    }
}
