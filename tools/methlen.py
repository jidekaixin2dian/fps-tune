#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""方法级长度分布统计（P3-8 固定口径，tools/ 固化版）。

用法：
    python tools/methlen.py FpsTune.Wpf            # 统计当前树
    python tools/methlen.py <目录> --top 10        # 看最长前 10

口径（2026-09-28 定稿；用 0.1.4 树 e3d0041 校准）：
  - 范围：目录下全部 .cs（自动排除 obj/、bin/、*.g.cs、*.g.i.cs、AssemblyInfo、.Designer.cs）
  - "方法" = 带修饰符的成员函数/构造函数，含表达式体成员；不含属性访问器、字段、lambda、局部函数
  - 跨行签名完整统计；注释/字符串感知的大括号配对
  - 长度 = 声明行到收尾大括号行（含两端）的物理行数；表达式体成员按实际占行数计
  - 校准：0.1.4 树上 7 个点名方法长度逐一精确复现（180/167/154/125/115/108/103）
  - 旧记录（0.1.4 时点 590/14/29/7/max180）已证实出自带缺陷的一次性逐行正则脚本：
    要求完整单行签名 + 返回类型、不数构造函数与表达式体、朴素大括号计数，
    因此漏掉 RestoreAllCore(248) 等跨行签名方法，"最长 180"结论失真。
    此后以本脚本口径为准，不要再引用旧总数。
输出 JSON：total / median / ge60 / ge60_pct / ge100 / max / top 列表。
"""
import argparse
import json
import re
import statistics
import sys
from pathlib import Path

MODS = r"(?:public|private|internal|protected|static|async|sealed|override|virtual|abstract|extern|unsafe|partial|new)"
# 带修饰符的成员签名：mods + 返回类型 + 名称 + (
SIG = re.compile(r"^(?:(?:" + MODS + r")\s+)+(?:[\w.<>\[\],?]+\s+)?(?P<name>\w+)\s*\(")
# 无修饰符签名（局部函数用）：返回类型 + 名称 + (，名称不能是控制关键字
SIG_BARE = re.compile(r"^(?:[\w.<>\[\],?]+\s+)?(?P<name>\w+)\s*\(")
TYPE_DECL = re.compile(r"\b(class|struct|interface|enum|record)\s+(\w+)")
CONTROL_KW = {"if", "for", "foreach", "while", "switch", "using", "lock",
              "catch", "do", "return", "throw", "else", "try", "finally", "case", "new"}
GENERATED = re.compile(r"(?:^|[\\/])(?:obj|bin)(?:[\\/]|$)|\.g\.i?\.cs$|AssemblyInfo\.cs$|\.Designer\.cs$")


def has_top_level_eq(acc: str) -> bool:
    """acc 的括号外是否含 '='（方法声明除非默认参数值，不会在括号外出现 '='）。"""
    depth = 0
    for ch in acc:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        elif ch == "=" and depth == 0 and not acc[max(0, acc.find(ch) - 1):acc.find(ch) + 2].endswith(("==", ">=", "<=", "!=")):
            return True
    return False


def scan_file(path: Path, include_ctors=True, include_local=False, include_expr=True):
    """返回 [(name, start_line1, length)]。行号 1 基。"""
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    lines = text.splitlines()
    out = []
    stack = []            # 每层 {'kind': namespace|type|method|other, 'name':...}
    acc = ""              # 当前语句（已去注释/字符串）
    acc_start = 0         # 语句首行（1 基）
    cur_type = None       # 最近一层 type 名（构造函数识别用）

    def statement_begin(i):
        nonlocal acc, acc_start
        if not acc.strip():
            acc_start = i

    for idx, raw in enumerate(lines):
        i = idx + 1
        line = raw
        if line.lstrip().startswith("#"):
            continue  # 预处理指令行不参与语句累计
        j, n = 0, len(line)
        mode = None  # None | 'line' | 'block' | 'str' | 'vstr' | 'chr'
        while j < n:
            ch = line[j]
            nxt = line[j + 1] if j + 1 < n else ""
            if mode is None:
                if ch == "/" and nxt == "/":
                    break
                if ch == "/" and nxt == "*":
                    mode = "block"
                    j += 2
                    continue
                if ch == '"':
                    mode = "vstr" if line[j:j + 2] == '@"' else "str"
                    acc += " "
                    j += 2 if mode == "vstr" else 1
                    continue
                if ch == "'":
                    mode = "chr"
                    acc += " "
                    j += 1
                    continue
                statement_begin(i)
                acc += ch
                if ch == "{":
                    kind, name = "other", None
                    top = stack[-1]["kind"] if stack else "namespace"
                    stripped = acc.strip()
                    m_type = TYPE_DECL.search(stripped)
                    can_member = top in ("namespace", "type")
                    if can_member and m_type and not stripped.rstrip().endswith(";"):
                        kind, name = "type", m_type.group(2)
                    elif can_member and SIG.match(stripped) and not has_top_level_eq(stripped):
                        kind, name = "method", SIG.match(stripped).group("name")
                    elif (include_ctors and can_member and cur_type
                          and re.match(r"^(?:(?:" + MODS + r")\s+)*" + re.escape(cur_type) + r"\s*\($", stripped)):
                        kind, name = "method", cur_type
                    elif include_local and top == "method":
                        m = SIG_BARE.match(stripped)
                        if (m and m.group("name") not in CONTROL_KW
                                and not has_top_level_eq(stripped) and "(" in stripped):
                            kind, name = "method", m.group("name")
                    stack.append({"kind": kind, "name": name, "start": acc_start if kind == "method" else i})
                    if kind == "type":
                        cur_type = name
                    acc = ""
                elif ch == "}":
                    if stack:
                        top = stack.pop()
                        if top["kind"] == "method":
                            out.append((top["name"], top["start"], i - top["start"] + 1))
                        if top["kind"] == "type":
                            # 回到该 type 外层：恢复外层 type 名
                            cur_type = next((f["name"] for f in reversed(stack) if f["kind"] == "type"), None)
                    acc = ""
                elif ch == ";":
                    stripped = acc.strip()
                    if include_expr and "=>" in stripped and SIG.match(stripped):
                        m = SIG.match(stripped)
                        out.append((m.group("name"), acc_start, i - acc_start + 1))
                    acc = ""
                j += 1
                continue
            if mode == "line":
                break
            if mode == "block":
                if ch == "*" and nxt == "/":
                    mode = None
                    j += 2
                    continue
            elif mode == "str":
                if ch == "\\":
                    j += 2
                    continue
                if ch == '"':
                    mode = None
            elif mode == "vstr":
                if ch == '"':
                    if nxt == '"':
                        j += 2
                        continue
                    mode = None
            elif mode == "chr":
                if ch == "\\":
                    j += 2
                    continue
                if ch == "'":
                    mode = None
            j += 1
    return out


def collect(root: Path):
    files = sorted(p for p in root.rglob("*.cs") if not GENERATED.search(str(p)))
    return files


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("--top", type=int, default=8)
    ap.add_argument("--no-ctors", action="store_true")
    ap.add_argument("--include-local", action="store_true")
    ap.add_argument("--no-expr", action="store_true")
    ap.add_argument("--all", action="store_true", help="打印全部方法（校准用）")
    args = ap.parse_args()

    root = Path(args.root)
    files = collect(root)
    methods = []
    for f in files:
        rel = f.relative_to(root).as_posix()
        for name, start, length in scan_file(f, include_ctors=not args.no_ctors,
                                             include_local=args.include_local,
                                             include_expr=not args.no_expr):
            methods.append({"file": rel, "name": name, "line": start, "len": length})

    lens = [m["len"] for m in methods]
    lens.sort()
    result = {
        "files": len(files),
        "total": len(methods),
        "median": statistics.median(lens) if lens else 0,
        "ge60": sum(1 for x in lens if x >= 60),
        "ge60_pct": round(100 * sum(1 for x in lens if x >= 60) / len(lens), 1) if lens else 0,
        "ge100": sum(1 for x in lens if x >= 100),
        "max": max(lens) if lens else 0,
        "top": sorted(methods, key=lambda m: -m["len"])[:args.top],
    }
    print(json.dumps(result, ensure_ascii=False, indent=1))
    if args.all:
        for m in sorted(methods, key=lambda m: -m["len"]):
            print(f"{m['len']:>4}  {m['file']}:{m['line']}  {m['name']}")


if __name__ == "__main__":
    sys.exit(main())
