// Pull the JSON-RPC parameter helpers + path-sandbox helpers in as plain
// names so existing handler call sites (RequireString, EnsurePathInsideProject,
// NormaliseSeparators…) keep compiling unchanged. Helpers live in the
// shared Studio.Rpc library so the standalone Jiangyu.Mcp binary calls
// the same code.
global using static Jiangyu.Studio.Rpc.RpcHelpers;
