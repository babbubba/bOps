// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

namespace bOps.Api;

internal static class ApiAuthorization
{
    public const string ViewerPolicy = "bops.viewer";
    public const string OperatorPolicy = "bops.operator";
    public const string ApproverPolicy = "bops.approver";

    public const string ViewerRole = "viewer";
    public const string OperatorRole = "operator";
    public const string ApproverRole = "approver";
}
