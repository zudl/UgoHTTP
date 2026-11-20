using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

// Type, field and constant names mirror native/include/ugo.h exactly so the layout dump diffs line by line.
namespace UgoHttp.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_bytes
    {
        public ulong ptr;
        public ulong len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_client_config
    {
        public uint abi_version;
        public uint max_transfers;
        public uint max_total_connections;
        public uint dns_cache_ttl_s;
        public uint flags;
        public uint reserved;
        public ugo_bytes ca_bundle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_mime_part
    {
        public ugo_bytes name;
        public ugo_bytes filename;
        public ugo_bytes content_type;
        public ugo_bytes data;
        public uint source;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_request
    {
        public ugo_bytes method;
        public ugo_bytes url;
        public ugo_bytes headers;
        public ugo_bytes body;
        public ulong parts;
        public uint part_count;
        public uint body_kind;
        public ugo_bytes sink_path;
        public ulong memory_limit;
        public ulong max_recv_bytes_per_s;
        public ulong max_send_bytes_per_s;
        public uint low_speed_bytes_per_s;
        public uint low_speed_time_s;
        public uint connect_timeout_ms;
        public uint total_timeout_ms;
        public uint max_redirects;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_ws_request
    {
        public ugo_bytes url;
        public ugo_bytes headers;
        public uint connect_timeout_ms;
        public uint open_timeout_ms;
        public uint close_timeout_ms;
        public uint max_message_bytes;
        public ulong max_backlog_bytes;
        public uint max_backlog_count;
        public uint max_send_bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_result
    {
        public ugo_bytes body;
        public ugo_bytes headers;
        public ugo_bytes error_message;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_ev_headers
    {
        public uint http_status;
        public uint http_version;
        public long content_length;
        public ugo_bytes headers;
        public ulong resume_offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_ev_progress
    {
        public ulong received;
        public long receive_total;
        public ulong sent;
        public long send_total;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_ev_completed
    {
        public uint error_class;
        public int curl_code;
        public uint http_status;
        public uint http_version;
        public ulong bytes_received;
        public ulong bytes_sent;
        public uint connection_reused;
        public uint redirect_count;
        public uint detail;
        public uint ws_close_code;
        public long queue_us;
        public long namelookup_us;
        public long connect_us;
        public long appconnect_us;
        public long starttransfer_us;
        public long total_us;
        public long redirect_us;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ugo_ev_ws_message
    {
        public ulong seq;
        public ugo_bytes data;
        public uint type;
        public uint reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ugo_event
    {
        public uint kind;
        public uint reserved;
        public ulong id;
        public long time_us;
        public fixed ulong u[13];
    }

    internal static class UgoAbi
    {
        public const uint UGO_ABI_VERSION = 2;
        public const ulong UGO_INVALID_ID = 0;
        public const uint UGO_WS_CLOSE_REASON_MAX = 123;
        public const uint UGO_METHOD_MAX = 32;
        public const uint UGO_ERROR_BODY_MAX = 65536;

        public const int UGO_OK = 0;
        public const int UGO_E_INVALID_ARG = -1;
        public const int UGO_E_STALE_ID = -2;
        public const int UGO_E_STATE = -3;
        public const int UGO_E_CAPACITY = -4;
        public const int UGO_E_BUSY = -5;
        public const int UGO_E_SHUTDOWN = -6;
        public const int UGO_E_ABI = -7;
        public const int UGO_E_NOMEM = -8;

        public const uint UGO_CLASS_NONE = 0;
        public const uint UGO_CLASS_DNS = 1;
        public const uint UGO_CLASS_CONNECT = 2;
        public const uint UGO_CLASS_TLS = 3;
        public const uint UGO_CLASS_TIMEOUT = 4;
        public const uint UGO_CLASS_TRANSFER = 5;
        public const uint UGO_CLASS_HTTP_STATUS = 6;
        public const uint UGO_CLASS_ABORTED = 7;
        public const uint UGO_CLASS_LOCAL_IO = 8;
        public const uint UGO_CLASS_INTERNAL = 9;

        public const uint UGO_DETAIL_NONE = 0;
        public const uint UGO_DETAIL_CALLER = 1;
        public const uint UGO_DETAIL_SHUTDOWN = 2;
        public const uint UGO_DETAIL_BACKGROUND_EXPIRED = 3;
        public const uint UGO_DETAIL_MEMORY_LIMIT = 4;
        public const uint UGO_DETAIL_WS_HANDSHAKE = 5;
        public const uint UGO_DETAIL_WS_CLOSED_BY_LOCAL = 6;
        public const uint UGO_DETAIL_WS_CLOSED_BY_REMOTE = 7;
        public const uint UGO_DETAIL_RESUME_MISMATCH = 8;

        public const uint UGO_BODY_NONE = 0;
        public const uint UGO_BODY_MEMORY = 1;
        public const uint UGO_BODY_FILE = 2;
        public const uint UGO_BODY_MULTIPART = 3;

        public const uint UGO_PART_MEMORY = 0;
        public const uint UGO_PART_FILE = 1;

        public const uint UGO_REQ_KEEP_PARTIAL = 1u << 0;
        public const uint UGO_REQ_DETACHED = 1u << 1;
        public const uint UGO_REQ_FINISH_IN_BACKGROUND = 1u << 2;
        public const uint UGO_REQ_RESUME = 1u << 3;

        public const uint UGO_CLIENT_TLS_INSECURE_DEV = 1u << 0;

        public const uint UGO_HTTP_NONE = 0;
        public const uint UGO_HTTP_1_0 = 10;
        public const uint UGO_HTTP_1_1 = 11;
        public const uint UGO_HTTP_2 = 20;

        public const uint UGO_EV_HEADERS = 1;
        public const uint UGO_EV_PROGRESS = 2;
        public const uint UGO_EV_COMPLETED = 3;
        public const uint UGO_EV_WS_MESSAGE = 4;

        public const uint UGO_WS_TEXT = 1;
        public const uint UGO_WS_BINARY = 2;

        // Same line format as layout_check.c built with UGO_LAYOUT_PRINT; the gate diffs the two.
        internal static List<string> Dump()
        {
            var lines = new List<string>(160);
            Layout<ugo_bytes>(lines);
            Layout<ugo_client_config>(lines);
            Layout<ugo_mime_part>(lines);
            Layout<ugo_request>(lines);
            Layout<ugo_ws_request>(lines);
            Layout<ugo_result>(lines);
            Layout<ugo_ev_headers>(lines);
            Layout<ugo_ev_progress>(lines);
            Layout<ugo_ev_completed>(lines);
            Layout<ugo_ev_ws_message>(lines);
            Layout<ugo_event>(lines);
            foreach (var f in typeof(UgoAbi).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral)
                    lines.Add($"{f.Name} {Convert.ToInt64(f.GetRawConstantValue())}");
            return lines;
        }

        // A stride that differs from the marshalled size would misplace every event after the first in the drain buffer.
        static unsafe void Layout<T>(List<string> lines) where T : unmanaged
        {
            Type t = typeof(T);
            int size = Marshal.SizeOf<T>();
            lines.Add(size == sizeof(T) ? $"{t.Name} {size}" : $"{t.Name} {size} stride {sizeof(T)}");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                lines.Add($"{t.Name}.{f.Name} {(int)Marshal.OffsetOf(t, f.Name)}");
        }
    }
}
