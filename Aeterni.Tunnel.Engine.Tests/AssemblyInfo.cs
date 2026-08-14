using Xunit;

// 禁用测试并行：网络测试大量使用随机端口，并行会产生端口竞争（偶发失败）
[assembly: CollectionBehavior(DisableTestParallelization = true)]
