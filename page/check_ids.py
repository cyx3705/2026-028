#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""静态自检：页面里 $("xxx") 引用的每个元素，HTML 里必须真的存在。

为什么需要：
  加一个按钮却忘了写对应的 id，或者改 id 时漏了一处，
  页面会在启动那一刻抛 TypeError —— 而 "use strict" 下这会让
  后面所有初始化全部不执行。表现就是"点了没反应"，
  但真实原因跟点击毫无关系。这个检查能在打开浏览器之前就抓住它。

用法：
    python check_ids.py [chat.html]
"""

import re
import sys


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "chat.html"
    s = open(path, encoding="utf-8").read()

    # 只取 <script> 里的内容，避免把注释里的示例当成真实引用
    scripts = "\n".join(re.findall(r"<script>(.*?)</script>", s, re.S))
    body = re.sub(r"<script>.*?</script>", "", s, flags=re.S)

    used = set(re.findall(r'\$\("([A-Za-z0-9_-]+)"\)', scripts))
    have = set(re.findall(r'id="([A-Za-z0-9_-]+)"', body))

    missing = sorted(used - have)
    unused = sorted(have - used)

    print("脚本引用元素 %d 个，HTML 定义 id %d 个" % (len(used), len(have)))
    if missing:
        print("")
        print("[失败] 引用了但 HTML 里没有的 id（会在启动时抛 TypeError）:")
        for m in missing:
            print("   " + m)
    if unused:
        print("")
        print("提示：定义了但脚本没引用的 id（可能是给 CSS 或外部用的）:")
        print("   " + ", ".join(unused))

    if missing:
        return 1
    print("")
    print("[通过] 所有引用的元素都存在。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
