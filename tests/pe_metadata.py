import struct


def _rva_to_offset(data, section_table, section_count, rva):
    for index in range(section_count):
        offset = section_table + index * 40
        virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from("<IIII", data, offset + 8)
        span = max(virtual_size, raw_size)
        if virtual_address <= rva < virtual_address + span:
            delta = rva - virtual_address
            if delta >= raw_size:
                raise ValueError("PE RVA has no raw-data backing")
            return raw_pointer + delta
    raise ValueError("PE RVA does not map to a section")


def codeview_paths(data):
    if len(data) < 64 or data[:2] != b"MZ":
        return []
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("invalid PE signature")
    coff = pe + 4
    section_count = struct.unpack_from("<H", data, coff + 2)[0]
    optional_size = struct.unpack_from("<H", data, coff + 16)[0]
    optional = coff + 20
    magic = struct.unpack_from("<H", data, optional)[0]
    if magic == 0x20B:
        directory_count_offset, directory_offset = optional + 108, optional + 112
    elif magic == 0x10B:
        directory_count_offset, directory_offset = optional + 92, optional + 96
    else:
        raise ValueError("unsupported PE optional-header magic")
    directory_count = struct.unpack_from("<I", data, directory_count_offset)[0]
    if directory_count <= 6:
        return []
    debug_rva, debug_size = struct.unpack_from("<II", data, directory_offset + 6 * 8)
    if not debug_rva or not debug_size:
        return []
    section_table = optional + optional_size
    debug_offset = _rva_to_offset(data, section_table, section_count, debug_rva)
    if debug_size % 28:
        raise ValueError("invalid PE debug-directory size")
    paths = []
    for offset in range(debug_offset, debug_offset + debug_size, 28):
        debug_type = struct.unpack_from("<I", data, offset + 12)[0]
        data_size = struct.unpack_from("<I", data, offset + 16)[0]
        data_pointer = struct.unpack_from("<I", data, offset + 24)[0]
        if debug_type != 2 or not data_pointer:
            continue
        if data_pointer + data_size > len(data) or data_size < 25:
            raise ValueError("invalid PE CodeView record bounds")
        if data[data_pointer:data_pointer + 4] != b"RSDS":
            continue
        start = data_pointer + 24
        end = data.find(b"\0", start, data_pointer + data_size)
        if end < 0:
            raise ValueError("unterminated PE CodeView path")
        paths.append((start, end, data[start:end]))
    return paths
