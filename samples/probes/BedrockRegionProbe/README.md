# Bedrock region certification probe

This unshipped executable is a child-process fixture for the Infrastructure certification suite. It creates configuration objects only; it never constructs an AWS client or contacts a service.

`EgressRedirectHardeningTests.CloudBedrock_TheRuntimeClientsConfigurationNeverFollowsARedirect` is its sole caller. The test supplies empty profile files and controlled environment variables, checks both absent and synthetic region defaults, and verifies the child runtime and factory assembly digest. Build and run it through that suite, not as a standalone test project.
