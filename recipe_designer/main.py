import os
import re
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog
import webbrowser

GRID_SIZE = 3
CELL_PX = 64  # square cell size

# ------------------------ Discovery logging ------------------------
def _print(msg: str):
    print(f"[Discovery] {msg}", flush=True)

def _candidate_list_from_root(root: str) -> list[str]:
    root = os.path.abspath(os.path.expanduser(os.path.expandvars(root)))
    return [
        os.path.join(root, "assets", "survival"),
        os.path.join(root, "Vintagestory", "assets", "survival"),
        os.path.join(os.path.dirname(root), "Vintagestory", "assets", "survival"),
        root,  # in case user points directly at .../assets/survival
    ]

def _validate_survival_dir(path: str) -> bool:
    return os.path.isdir(path) and os.path.isdir(os.path.join(path, "itemtypes"))

def resolve_survival_dir(env_key: str | None, direct_root: str | None) -> tuple[str | None, list[str]]:
    logs: list[str] = []
    def log(line): logs.append(line); _print(line)

    log("---- Starting discovery ----")

    if direct_root:
        expanded = os.path.abspath(os.path.expanduser(os.path.expandvars(direct_root)))
        if not os.path.isabs(direct_root):
            log(f"Direct Search path is not absolute: {direct_root!r}")
        log(f"Direct Search path provided: {direct_root!r} (expanded: {expanded!r})")
        for cand in _candidate_list_from_root(expanded):
            ok = _validate_survival_dir(cand)
            log(f"  Try: {cand}  -> {'OK' if ok else 'nope'}")
            if ok:
                log(f"[FOUND] Using: {cand}")
                return cand, logs

    if env_key:
        val = os.environ.get(env_key)
        if not val:
            log(f"Env {env_key}: not set")
        else:
            log(f"Env {env_key}: {val!r}")
            for cand in _candidate_list_from_root(val):
                ok = _validate_survival_dir(cand)
                log(f"  Try: {cand}  -> {'OK' if ok else 'nope'}")
                if ok:
                    log(f"[FOUND] Using: {cand}")
                    return cand, logs

    log("[FAILED] Could not locate 'assets/survival'. Use Discovery → Direct Search… or set your env var.")
    return None, logs

# ------------------------ JSON5-ish text scanning ------------------------
_STR_RE = r'(?:"([^"]+)"|\'([^\']+)\')'  # match "..." or '...'

def _norm_pattern(s: str) -> str:
    s = re.sub(r"\{[^}]+\}", "*", s)    # {metal} -> *
    s = re.sub(r"\*{2,}", "*", s)       # collapse ** -> *
    return s.strip()

def _with_domain(code: str) -> str:
    return code if ":" in code else f"game:{code}"

def _discover_codes_in_text(text: str) -> set[str]:
    codes: set[str] = set()
    # 1) code: "foo"
    for m in re.finditer(r'\bcode\s*:\s*' + _STR_RE, text):
        val = m.group(1) or m.group(2)
        if not val: continue
        base = _with_domain(_norm_pattern(val))
        codes.add(base)
        if not base.endswith("*") and "-*" not in base:
            codes.add(base + "-*")
    # 2) code: { path: "foo-*" }
    for m in re.finditer(r'\bcode\s*:\s*\{[^{}]*?\bpath\s*:\s*' + _STR_RE, text, flags=re.S):
        val = m.group(1) or m.group(2)
        if not val: continue
        codes.add(_with_domain(_norm_pattern(val)))
    # 3) allowedVariants: ["foo-*", ...]
    for m in re.finditer(r'\ballowedVariants\s*:\s*\[(.*?)\]', text, flags=re.S):
        body = m.group(1) or ""
        for sm in re.finditer(_STR_RE, body):
            val = sm.group(1) or sm.group(2)
            if not val: continue
            codes.add(_with_domain(_norm_pattern(val)))
    # 4) groupBy: ["foo-*", ...]
    for m in re.finditer(r'\bgroupBy\s*:\s*\[(.*?)\]', text, flags=re.S):
        body = m.group(1) or ""
        for sm in re.finditer(_STR_RE, body):
            val = sm.group(1) or sm.group(2)
            if not val: continue
            codes.add(_with_domain(_norm_pattern(val)))
    return codes

def _discover_codes_in_file(path: str) -> set[str]:
    try:
        with open(path, "r", encoding="utf-8") as f:
            txt = f.read()
        return _discover_codes_in_text(txt)
    except Exception:
        return set()

# Build variant corpus (e.g., "game:ingot-" -> {"copper","iron",...})
def _collect_corpus_variants(survival_path: str) -> dict[str, set[str]]:
    variants: dict[str, set[str]] = {}
    rex_dom = re.compile(r'\b([a-z0-9_]+):([a-z0-9_]+)-([a-z0-9_]+)\b', re.I)
    rex_nodom = re.compile(r'\b([a-z0-9_]+)-([a-z0-9_]+)\b', re.I)

    def add(dom: str, base: str, token: str):
        if not base or not token: return
        key = f"{dom}:{base}-"
        variants.setdefault(key, set()).add(token)

    for kind in ("itemtypes", "blocktypes"):
        root = os.path.join(survival_path, kind)
        if not os.path.isdir(root):
            continue
        for dirpath, _dirnames, filenames in os.walk(root):
            for fn in filenames:
                if not (fn.endswith(".json") or fn.endswith(".json5")):
                    continue
                full = os.path.join(dirpath, fn)
                try:
                    with open(full, "r", encoding="utf-8") as f:
                        txt = f.read()
                except Exception:
                    continue

                for m in rex_dom.finditer(txt):
                    dom, base, token = m.group(1).lower(), m.group(2), m.group(3)
                    add(dom, base, token)
                for m in rex_nodom.finditer(txt):
                    base, token = m.group(1), m.group(2)
                    if "*" in base or "*" in token: 
                        continue
                    add("game", base, token)
    return variants

def discover_game_codes(env_key: str | None, direct_root: str | None) -> tuple[list[str], str | None]:
    survival, _logs = resolve_survival_dir(env_key, direct_root)
    codes: set[str] = set()
    if not survival:
        return [], None

    for kind in ("itemtypes", "blocktypes"):
        root = os.path.join(survival, kind)
        if not os.path.isdir(root):
            continue
        for dirpath, _dirnames, filenames in os.walk(root):
            for fn in filenames:
                if not (fn.endswith(".json") or fn.endswith(".json5")):
                    continue
                full = os.path.join(dirpath, fn)
                found = _discover_codes_in_file(full)
                if found:
                    codes.update(found)

    _print(f"Discovered {len(codes)} codes from: {survival}")
    return sorted(codes), survival

# ------------------------ Autocomplete widgets ------------------------
class AutocompleteList(tk.Toplevel):
    """Floating listbox below an Entry; supports Up/Down and Enter to commit."""
    def __init__(self, master, anchor_widget, items, on_select, width=50, height=8):
        super().__init__(master)
        self.withdraw()
        self.overrideredirect(True)
        self.items_all = sorted(set(items))
        self.on_select = on_select

        self.listbox = tk.Listbox(self, width=width, height=height, activestyle="dotbox")
        self.listbox.pack(fill="both", expand=True)
        self.listbox.bind("<Return>", self._commit)
        self.listbox.bind("<Double-1>", self._commit)
        self.listbox.bind("<Escape>", lambda e: self.destroy())

        self.anchor = anchor_widget
        self.update_list("")
        self.position()

    def position(self):
        try:
            x = self.anchor.winfo_rootx()
            y = self.anchor.winfo_rooty() + self.anchor.winfo_height()
            self.geometry(f"+{x}+{y}")
            self.deiconify()
        except Exception:
            pass

    def update_list(self, typed: str):
        typed = (typed or "").lower()
        self.listbox.delete(0, tk.END)
        for it in self.items_all:
            if typed in it.lower():
                self.listbox.insert(tk.END, it)
        if self.listbox.size() > 0:
            self.listbox.selection_clear(0, tk.END)
            self.listbox.selection_set(0)
            self.listbox.activate(0)

    def move_selection(self, delta: int):
        if self.listbox.size() == 0:
            return
        try:
            cur = self.listbox.curselection()
            idx = cur[0] if cur else 0
        except Exception:
            idx = 0
        idx = max(0, min(self.listbox.size() - 1, idx + delta))
        self.listbox.selection_clear(0, tk.END)
        self.listbox.selection_set(idx)
        self.listbox.activate(idx)
        self.listbox.see(idx)

    def _commit(self, _evt=None):
        if self.listbox.curselection():
            value = self.listbox.get(self.listbox.curselection()[0])
            self.on_select(value)
            self.destroy()

class AutocompleteEntry(ttk.Entry):
    """Entry with dropdown autocomplete; Up/Down navigate, Enter commits."""
    def __init__(self, master, items_provider, **kw):
        super().__init__(master, **kw)
        self.items_provider = items_provider
        self.popup: AutocompleteList | None = None
        self.bind("<KeyRelease>", self._on_key_release)
        self.bind("<FocusOut>", lambda e: self._close_popup())
        self.bind("<Down>", self._on_down)
        self.bind("<Up>", self._on_up)
        self.bind("<Return>", self._on_return)

    def _open_or_update(self, query: str):
        items = self.items_provider() or []
        if not items:
            self._close_popup()
            return
        if self.popup is None or not self.popup.winfo_exists():
            self.popup = AutocompleteList(self, self, items, self._set_value)
        self.popup.update_list(query)

    def _on_key_release(self, evt):
        if evt.keysym in ("Up", "Down", "Return", "Escape"):
            return
        self._open_or_update(self.get())

    def _on_down(self, evt):
        if self.popup and self.popup.winfo_exists():
            self.popup.move_selection(+1)
        else:
            self._open_or_update(self.get())
        return "break"

    def _on_up(self, evt):
        if self.popup and self.popup.winfo_exists():
            self.popup.move_selection(-1)
        else:
            self._open_or_update(self.get())
        return "break"

    def _on_return(self, evt):
        if self.popup and self.popup.winfo_exists() and self.popup.listbox.size() > 0:
            self.popup._commit()
            return "break"

    def _set_value(self, value):
        self.delete(0, tk.END)
        self.insert(0, value)
        self.focus_set()
        self._close_popup()

    def _close_popup(self):
        if self.popup is not None and self.popup.winfo_exists():
            self.popup.destroy()
        self.popup = None

# ------------------------ Data ------------------------
class Ingredient:
    def __init__(self, code="", quantity=1, is_tool=False, tool_cost=0, symbol=None, name="", allowed_variants=None):
        self.code = code
        self.quantity = quantity
        self.is_tool = is_tool
        self.tool_cost = tool_cost
        self.symbol = symbol  # A–Z
        self.name = (name or "").strip()
        self.allowed_variants = list(allowed_variants or [])

# ------------------------ Square grid cell ------------------------
class CellCanvas(tk.Canvas):
    def __init__(self, master, on_click, on_double_click, **kw):
        super().__init__(master, width=CELL_PX, height=CELL_PX, highlightthickness=0, **kw)
        self.on_click = on_click
        self.on_double_click = on_double_click
        self.selected = False
        self.symbol = " "
        self._draw()
        self.bind("<Button-1>", self._handle_click)
        self.bind("<Double-Button-1>", self._handle_double)

    def _draw(self):
        self.delete("all")
        bg = "#e3e3e3" if self.selected else "#f4f4f4"
        self.create_rectangle(2, 2, CELL_PX - 2, CELL_PX - 2,
                              width=2, outline="#777" if not self.selected else "#333",
                              fill=bg)
        if self.selected:
            self.create_line(2, 2, CELL_PX - 2, 2)
            self.create_line(2, 2, 2, CELL_PX - 2)
        self.create_text(CELL_PX // 2, CELL_PX // 2, text=self.symbol, font=("TkDefaultFont", 16, "bold"))

    def set_symbol(self, s: str):
        self.symbol = s or " "
        self._draw()

    def set_selected(self, sel: bool):
        self.selected = sel
        self._draw()

    def _handle_click(self, _e):
        if callable(self.on_click): self.on_click()

    def _handle_double(self, _e):
        if callable(self.on_double_click): self.on_double_click()

# ------------------------ Main App ------------------------
class RecipeBuilderApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Vintage Story Recipe Designer")
        self.resizable(False, False)

        # Discovery state
        self.env_key = "VINTAGE_STORY"
        self.direct_root = None
        self.discovered_items, self.used_survival_path = discover_game_codes(self.env_key, self.direct_root)

        # Variant corpus
        self.variant_index: dict[str, set[str]] = {}
        if self.used_survival_path:
            self.variant_index = _collect_corpus_variants(self.used_survival_path)
            _print(f"Variant corpus built: {sum(len(v) for v in self.variant_index.values())} tokens across {len(self.variant_index)} bases")

        # App state
        self.custom_items: list[str] = []
        self.items_cache: list[str] | None = None
        self.grid_items: list[list[Ingredient | None]] = [[None for _ in range(GRID_SIZE)] for _ in range(GRID_SIZE)]
        self.selected = (0, 0)
        self.shapeless_var = tk.BooleanVar(value=False)
        self.output_code_var = tk.StringVar()
        self.output_qty_var = tk.StringVar(value="1")
        self.code_to_symbol: dict[str, str] = {}

        # UI
        self._build_menu()
        self._build_top()
        self._build_main()
        self._build_bottom()

        # Global keys
        self.bind("<Delete>", self._delete_selected)
        self.bind("<Return>", self._edit_selected)

        # Initial selection + discovery label
        self._select_cell(0, 0)
        self._update_discovery_status()
        self._update_discovery_menu_label()

    # ---------- Menu ----------
    def _build_menu(self):
        self.menubar = tk.Menu(self)

        # Discovery menu
        self.discovery_menu = tk.Menu(self.menubar, tearoff=0)
        self.discovery_menu.add_command(label="Change Environment…", command=self._change_env_key)
        self.discovery_menu.add_command(label="Direct Search…", command=self._direct_search)
        self.menubar.add_cascade(label="Discovery", menu=self.discovery_menu)
        self.discovery_menu_index = self.menubar.index("end")

        # Tools
        tools_menu = tk.Menu(self.menubar, tearoff=0)
        tools_menu.add_command(label="Reset", command=self.reset_all)
        self.menubar.add_cascade(label="Tools", menu=tools_menu)

        # Items
        items_menu = tk.Menu(self.menubar, tearoff=0)
        items_menu.add_command(label="Add custom items…", command=self.add_custom_items)
        self.menubar.add_cascade(label="Items", menu=items_menu)

        # Join the Modding Co-op!
        self.menubar.add_command(label="Join The Modding Co-op!", command=self._open_coop_link)

        self.config(menu=self.menubar)

    def _open_coop_link(self):
        webbrowser.open("https://discord.gg/6BMDKTJAuE")

    def _update_discovery_menu_label(self):
        label = "Discovery" if self.used_survival_path else "Discovery (!!!)"
        self.menubar.entryconfig(self.discovery_menu_index, label=label)

    # ---------- Top ----------
    def _build_top(self):
        top = ttk.Frame(self, padding=6)
        top.pack(fill="x")
        ttk.Checkbutton(top, text="Shapeless", variable=self.shapeless_var).pack(side="left")
        self.discovery_status = tk.StringVar(value="Discovery: (none)")
        ttk.Label(top, textvariable=self.discovery_status).pack(side="right")

    # ---------- Main (grid + side) ----------
    def _build_main(self):
        main = ttk.Frame(self, padding=6)
        main.pack()

        # Grid
        self.grid_frame = ttk.Frame(main)
        self.grid_frame.grid(row=0, column=0, sticky="n")

        self.cell_widgets: list[list[CellCanvas]] = []
        for r in range(GRID_SIZE):
            row_widgets = []
            for c in range(GRID_SIZE):
                cell = CellCanvas(
                    self.grid_frame,
                    on_click=lambda rr=r, cc=c: self._select_cell(rr, cc),
                    on_double_click=lambda rr=r, cc=c: self._open_cell_popup(rr, cc),
                )
                cell.grid(row=r, column=c, padx=4, pady=4)
                row_widgets.append(cell)
            self.cell_widgets.append(row_widgets)

        # Side panel
        self.side = ttk.LabelFrame(main, text="Selected Slot", padding=8)
        self.side.grid(row=0, column=1, padx=10, sticky="n")

        self.slot_pos_lbl = ttk.Label(self.side, text="Selected slot: 1×1")
        self.slot_pos_lbl.grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 4))

        ttk.Label(self.side, text="Item code:").grid(row=1, column=0, sticky="w")
        self.slot_code_val = ttk.Label(self.side, text="-", width=30)
        self.slot_code_val.grid(row=1, column=1, columnspan=2, sticky="w")

        self.is_tool_var = tk.BooleanVar(value=False)
        self.is_tool_chk = ttk.Checkbutton(self.side, text="Is Tool", variable=self.is_tool_var, command=self._on_is_tool_toggle)
        self.is_tool_chk.grid(row=2, column=0, sticky="w", pady=(6, 0))

        # Tool durability (hidden unless tool)
        self.tool_row = ttk.Frame(self.side)
        ttk.Label(self.tool_row, text="Tool durability cost:").grid(row=0, column=0, sticky="w")
        self.tool_cost_var = tk.StringVar(value="0")
        tool_cost_entry = ttk.Entry(self.tool_row, textvariable=self.tool_cost_var, width=8)
        tool_cost_entry.grid(row=0, column=1, sticky="w", padx=(6, 0))
        tool_cost_entry.bind("<KeyRelease>", lambda e: self._side_apply())
        self.tool_row.grid_remove()

        ttk.Label(self.side, text="Quantity:").grid(row=4, column=0, sticky="w", pady=(6, 0))
        self.qty_var = tk.StringVar(value="1")
        qty_entry = ttk.Entry(self.side, textvariable=self.qty_var, width=8)
        qty_entry.grid(row=4, column=1, sticky="w", pady=(6, 0))
        qty_entry.bind("<KeyRelease>", lambda e: self._side_apply())

        self.wildcard_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(self.side, text='Allow variants (*)', variable=self.wildcard_var, command=self._side_apply)\
            .grid(row=5, column=0, columnspan=3, sticky="w", pady=(6, 0))

        ttk.Label(self.side, text="Symbol (A–Z):").grid(row=6, column=0, sticky="w", pady=(6, 0))
        self.symbol_var = tk.StringVar(value="")
        sym_entry = ttk.Entry(self.side, textvariable=self.symbol_var, width=6)
        sym_entry.grid(row=6, column=1, sticky="w", pady=(6, 0))
        sym_entry.bind("<KeyRelease>", lambda e: self._apply_symbol_live())
        sym_entry.bind("<FocusOut>", lambda e: self._apply_symbol_live())

        # Slot name
        ttk.Label(self.side, text='Key (Name):').grid(row=7, column=0, sticky="w", pady=(6, 0))
        self.slot_name_var = tk.StringVar(value="")
        slotname_entry = ttk.Entry(self.side, textvariable=self.slot_name_var, width=16)
        slotname_entry.grid(row=7, column=1, sticky="w", pady=(6,0))
        slotname_entry.bind("<KeyRelease>", lambda e: self._apply_slot_name())

        # Variants row (hidden unless wildcard + options exist)
        self.variants_row = ttk.Frame(self.side)
        ttk.Label(self.variants_row, text="Variants:").grid(row=0, column=0, sticky="w")
        self.variants_btn = ttk.Button(self.variants_row, text="Choose…", command=lambda: None)
        self.variants_btn.grid(row=0, column=1, sticky="w", padx=(6, 0))
        self.variants_summary = ttk.Label(self.variants_row, text="")
        self.variants_summary.grid(row=0, column=2, sticky="w", padx=(6, 0))
        self.variants_row.grid_remove()

    # ---------- Bottom ----------
    def _build_bottom(self):
        outframe = ttk.Frame(self, padding=(6, 0, 6, 6))
        outframe.pack(fill="x")
        ttk.Label(outframe, text="Result item code:").grid(row=0, column=0, sticky="w", padx=(0, 6))
        self.result_entry = AutocompleteEntry(outframe, self._get_items, width=44, textvariable=self.output_code_var)
        self.result_entry.grid(row=0, column=1, sticky="w")
        ttk.Label(outframe, text="Qty:").grid(row=0, column=2, sticky="e", padx=(10, 4))
        qty = ttk.Entry(outframe, textvariable=self.output_qty_var, width=6)
        qty.grid(row=0, column=3, sticky="w")
        ttk.Button(outframe, text="Copy Json5", command=self.copy_json5).grid(row=0, column=4, padx=8)

    # ---------- Variant index ----------
    def _build_variant_index(self):
        self.variant_index.clear()
        if self.used_survival_path:
            self.variant_index = _collect_corpus_variants(self.used_survival_path)

    # ---------- Items pool ----------
    def _get_items(self):
        if self.items_cache is None:
            pool = []
            pool.extend(self.discovered_items)
            pool.extend(self.custom_items)
            self.items_cache = sorted(set(pool))
        return self.items_cache

    def add_custom_items(self):
        txt = simpledialog.askstring(
            "Add custom items",
            "Enter item/block codes separated by commas, spaces, or new lines.\n"
            "Example: mymod:widget, mymod:gizmo, game:ingot-*",
            parent=self
        )
        if not txt:
            return
        parts = [p.strip() for p in re.split(r"[,\s]+", txt) if p.strip()]
        if not parts:
            return
        self.custom_items.extend(parts)
        self.items_cache = None
        messagebox.showinfo("Items added", f"Added {len(parts)} item(s) to autocomplete.")

    # ---------- Discovery menu actions ----------
    def _change_env_key(self):
        val = simpledialog.askstring("Change Environment", "Env var name to search:", initialvalue=self.env_key, parent=self)
        if val is None:
            return
        self.env_key = val.strip() or self.env_key
        self._refresh_discovery()

    def _direct_search(self):
        val = simpledialog.askstring("Direct Search", "Absolute path to your Vintage Story folder (or assets/survival):", parent=self)
        if val is None:
            return
        val = os.path.expanduser(os.path.expandvars(val.strip()))
        if not os.path.isabs(val):
            messagebox.showerror("Path must be absolute",
                                 "Please provide an absolute path (e.g., C:\\Games\\Vintagestory or /home/user/Vintagestory).")
            return
        self.direct_root = val
        self._refresh_discovery()

    def _refresh_discovery(self):
        self.discovered_items, self.used_survival_path = discover_game_codes(self.env_key, self.direct_root)
        self._build_variant_index()
        self.items_cache = None
        self._update_discovery_status()
        self._update_discovery_menu_label()
        messagebox.showinfo("Discovery updated",
                            f"Found {len(self.discovered_items)} codes.\n"
                            f"Source: {self.used_survival_path or '— (none)'}")

    def _update_discovery_status(self):
        base = self.used_survival_path or "not found"
        self.discovery_status.set(f"Discovery: {base}")

    # ---------- Grid selection / editing ----------
    def _select_cell(self, r, c):
        self.selected = (r, c)
        for rr in range(GRID_SIZE):
            for cc in range(GRID_SIZE):
                self.cell_widgets[rr][cc].set_selected((rr, cc) == (r, c))
        self._update_side_for_selected()
        self.slot_pos_lbl.config(text=f"Selected slot: {c+1}×{r+1}")

    def _open_cell_popup(self, r, c):
        self._select_cell(r, c)
        popup = tk.Toplevel(self)
        popup.title("Set item")
        popup.resizable(False, False)
        ttk.Label(popup, text="Item code:").grid(row=0, column=0, padx=6, pady=6, sticky="w")
        code_var = tk.StringVar()
        entry = AutocompleteEntry(popup, self._get_items, width=50, textvariable=code_var)
        entry.grid(row=0, column=1, padx=6, pady=6)
        entry.focus_set()

        def ok():
            code = code_var.get().strip()
            if code:
                self._set_cell_item(r, c, code)
            popup.destroy()

        ttk.Button(popup, text="OK", command=ok).grid(row=1, column=0, columnspan=2, pady=(0, 8))
        popup.bind("<Return>", lambda e: ok())

    def _edit_selected(self, _evt=None):
        r, c = self.selected
        self._open_cell_popup(r, c)

    def _delete_selected(self, _evt=None):
        r, c = self.selected
        self.grid_items[r][c] = None
        self._refresh_cell(r, c)
        self._update_side_for_selected()

    def _set_cell_item(self, r, c, code: str):
        ing = self.grid_items[r][c] or Ingredient()
        ing.code = code
        if ing.symbol is None:
            ing.symbol = self._symbol_for_code(code)
        self.grid_items[r][c] = ing
        self._refresh_cell(r, c)
        self._update_side_for_selected()

    def _refresh_cell(self, r, c):
        ing = self.grid_items[r][c]
        sym = " " if ing is None or not ing.code else (ing.symbol or "?")
        self.cell_widgets[r][c].set_symbol(sym)

    # ---------- Side panel ----------
    def _update_side_for_selected(self):
        r, c = self.selected
        ing = self.grid_items[r][c]
        if ing is None:
            self.slot_code_val.config(text="-")
            self.is_tool_var.set(False)
            self.tool_cost_var.set("0")
            self.qty_var.set("1")
            self.wildcard_var.set(False)
            self.symbol_var.set("")
            self.slot_name_var.set("")
            self.variants_row.grid_remove()
            self.tool_row.grid_remove()
        else:
            self.slot_code_val.config(text=ing.code or "-")
            self.is_tool_var.set(bool(ing.is_tool))
            self.tool_cost_var.set(str(int(ing.tool_cost or 0)))
            self.qty_var.set(str(int(ing.quantity or 1)))
            self.wildcard_var.set(ing.code.endswith("*") if ing.code else False)
            self.symbol_var.set(ing.symbol or "")
            self.slot_name_var.set(ing.name or "")
            if ing.is_tool:
                self.tool_row.grid(row=3, column=0, columnspan=3, sticky="w", pady=(6, 0))
            else:
                self.tool_row.grid_remove()
            self._update_variant_ui()

    def _on_is_tool_toggle(self):
        r, c = self.selected
        ing = self.grid_items[r][c]
        if not ing: return
        ing.is_tool = bool(self.is_tool_var.get())
        if ing.is_tool: self.tool_row.grid(row=3, column=0, columnspan=3, sticky="w", pady=(6, 0))
        else:          self.tool_row.grid_remove()

    # --- Normalize wildcard code form based on allowed_variants content
    def _normalize_wildcard_code(self, ing: 'Ingredient'):
        """
        If ing.code ends with '*':
          - if ing.allowed_variants (non-empty): ensure 'base-*'
          - else: ensure 'base*'
        Never produce '--*'.
        """
        if not ing or not ing.code or not ing.code.endswith("*"):
            return
        base = ing.code[:-1]              # strip trailing '*'
        base = base.rstrip("-")           # remove any trailing dashes
        if ing.allowed_variants:
            ing.code = f"{base}-*"
        else:
            ing.code = f"{base}*"

    def _side_apply(self):
        r, c = self.selected
        ing = self.grid_items[r][c]
        if ing is None: return
        ing.is_tool = bool(self.is_tool_var.get())
        try:
            ing.tool_cost = max(0, int(self.tool_cost_var.get()))
        except ValueError:
            ing.tool_cost = 0
        try:
            ing.quantity = max(1, int(self.qty_var.get()))
        except ValueError:
            ing.quantity = 1

        # Handle wildcard code toggle
        if ing.code:
            if self.wildcard_var.get():
                if not ing.code.endswith("*"):
                    ing.code += "*"
                # normalize (may or may not add '-')
                self._normalize_wildcard_code(ing)
            else:
                if ing.code.endswith("*"):
                    ing.code = ing.code[:-1]
                # Clear variants if wildcard removed (keeps things coherent)
                ing.allowed_variants.clear()

        self.slot_code_val.config(text=ing.code or "-")
        self._refresh_cell(r, c)
        self._update_variant_ui()

    def _apply_slot_name(self):
        r, c = self.selected
        ing = self.grid_items[r][c]
        if ing is None:
            self.slot_name_var.set("")
            return
        ing.name = (self.slot_name_var.get() or "").strip()

    # ---------- Variants UI ----------
    def _update_variant_ui(self):
        r, c = self.selected
        ing = self.grid_items[r][c]
        self.variants_row.grid_remove()
        if not ing or not ing.code:
            return
        if not self.wildcard_var.get() or not ing.code.endswith("*"):
            return

        prefix = ing.code[:-1]  # strip trailing '*'
        # Ensure key form: "<domain>:<base>-"
        if ":" not in prefix:
            key = f"game:{prefix}-" if not prefix.endswith("-") else f"game:{prefix}"
        else:
            key = prefix if prefix.endswith("-") else prefix + "-"

        options = sorted(self.variant_index.get(key, []))
        if not options:
            return  # no variants known -> keep hidden

        def open_dialog():
            self._open_variants_dialog(ing, options)

        self.variants_btn.configure(command=open_dialog)
        self._update_variants_summary(ing, options)
        self.variants_row.grid(row=8, column=0, columnspan=3, sticky="w", pady=(6, 0))

    def _update_variants_summary(self, ing: Ingredient, options: list[str]):
        if not ing.allowed_variants:
            self.variants_summary.config(text="(none selected)")
            return
        sel = [v for v in ing.allowed_variants if v in options]
        if not sel:
            self.variants_summary.config(text="(none selected)")
            ing.allowed_variants = []
            # Also normalize code (will switch to 'base*')
            self._normalize_wildcard_code(ing)
            self.slot_code_val.config(text=ing.code or "-")
            return
        if len(sel) <= 5:
            self.variants_summary.config(text=", ".join(sel))
        else:
            self.variants_summary.config(text=f"{len(sel)} selected")

    def _open_variants_dialog(self, ing: Ingredient, options: list[str]):
        top = tk.Toplevel(self)
        top.title("Select Variants")
        top.resizable(False, False)

        checks = []
        selected = set(ing.allowed_variants or [])
        for i, opt in enumerate(options):
            var = tk.BooleanVar(value=(opt in selected))
            cb = ttk.Checkbutton(top, text=opt, variable=var)
            cb.grid(row=i, column=0, sticky="w", padx=8)
            checks.append((opt, var))

        btns = ttk.Frame(top)
        btns.grid(row=len(options), column=0, pady=6, sticky="w", padx=6)

        def set_all(val: bool):
            for _, v in checks:
                v.set(val)

        ttk.Button(btns, text="All", command=lambda: set_all(True)).grid(row=0, column=0, padx=4)
        ttk.Button(btns, text="None", command=lambda: set_all(False)).grid(row=0, column=1, padx=4)

        def ok():
            ing.allowed_variants = [opt for opt, v in checks if v.get()]
            # Normalize code form based on whether variants are specified
            if self.wildcard_var.get():
                if not ing.code.endswith("*"):
                    ing.code += "*"
                self._normalize_wildcard_code(ing)
                self.slot_code_val.config(text=ing.code or "-")
            self._update_variants_summary(ing, options)
            top.destroy()

        ttk.Button(btns, text="OK", command=ok).grid(row=0, column=2, padx=8)

    # ---------- Symbol editing (live) ----------
    def _apply_symbol_live(self):
        """Apply symbol change immediately; keep only first letter; auto-propagate to all slots with same code."""
        r, c = self.selected
        ing = self.grid_items[r][c]
        if ing is None:
            self.symbol_var.set("")
            return

        raw = (self.symbol_var.get() or "")
        first = ""
        for ch in raw:
            if ch.isalpha():
                first = ch.upper()
                break

        if not first:
            self.symbol_var.set(ing.symbol or "")
            return

        # Prevent duplicates across *different* codes
        for rr in range(GRID_SIZE):
            for cc in range(GRID_SIZE):
                other = self.grid_items[rr][cc]
                if not other or other is ing:
                    continue
                if other.code != ing.code and other.symbol == first:
                    self.symbol_var.set(ing.symbol or "")
                    messagebox.showerror("Duplicate symbol",
                                         f"Symbol '{first}' is already used by another ingredient.")
                    return

        # Commit + propagate
        ing.symbol = first
        if ing.code:
            self.code_to_symbol[ing.code] = first
            self._propagate_symbol_for_code(ing.code, first)

        if self.symbol_var.get() != first:
            self.symbol_var.set(first)

    def _propagate_symbol_for_code(self, code: str, letter: str):
        for rr in range(GRID_SIZE):
            for cc in range(GRID_SIZE):
                x = self.grid_items[rr][cc]
                if x and x.code == code:
                    x.symbol = letter
                    self._refresh_cell(rr, cc)

    # ---------- Symbol assignment for new items ----------
    def _symbol_for_code(self, code: str) -> str:
        used_letters = set()
        letters_by_code = {}
        for row in self.grid_items:
            for x in row:
                if x and x.symbol:
                    used_letters.add(x.symbol)
                    letters_by_code.setdefault(x.code, set()).add(x.symbol)

        if code in self.code_to_symbol:
            letter = self.code_to_symbol[code]
            if letter in used_letters and (code not in letters_by_code or letter not in letters_by_code.get(code, set())):
                pass
            else:
                return letter

        for i in range(26):
            candidate = chr(ord("A") + i)
            if candidate not in used_letters:
                self.code_to_symbol[code] = candidate
                return candidate
        self.code_to_symbol[code] = "X"
        return "X"

    # ---------- Reset ----------
    def reset_all(self):
        if not messagebox.askyesno("Reset", "Clear the grid and output?"):
            return
        self.grid_items = [[None for _ in range(GRID_SIZE)] for _ in range(GRID_SIZE)]
        self.code_to_symbol.clear()
        self.shapeless_var.set(False)
        self.output_code_var.set("")
        self.output_qty_var.set("1")
        for r in range(GRID_SIZE):
            for c in range(GRID_SIZE):
                self._refresh_cell(r, c)
        self._select_cell(0, 0)

    # ---------- JSON5 generation ----------
    def _build_ingredient_pattern(self) -> str:
        rows = []
        for r in range(GRID_SIZE):
            row = []
            for c in range(GRID_SIZE):
                ing = self.grid_items[r][c]
                row.append((ing.symbol or self._symbol_for_code(ing.code)) if (ing and ing.code) else "_")
            rows.append("".join(row))
        return ",".join(rows)

    def _collect_ingredients_dict(self) -> dict[str, 'Ingredient']:
        ing_map = {}
        for r in range(GRID_SIZE):
            for c in range(GRID_SIZE):
                ing = self.grid_items[r][c]
                if not ing or not ing.code:
                    continue
                sym = ing.symbol or self._symbol_for_code(ing.code)
                if sym not in ing_map:
                    ing_map[sym] = ing
        return ing_map

    def build_json5_text(self) -> str:
        ing_pattern = self._build_ingredient_pattern()
        ing_map = self._collect_ingredients_dict()

        lines = []
        lines.append("[")
        lines.append("\t{")
        lines.append(f'\t\tingredientPattern: "{ing_pattern}",')
        lines.append(f'\t\tshapeless: {"true" if self.shapeless_var.get() else "false"},')
        lines.append("\t\tingredients: {")
        for sym in sorted(ing_map.keys()):
            ing = ing_map[sym]
            inner = [f'type: "item"', f'code: "{ing.code}"']
            if ing.name:
                inner.append(f'name: "{ing.name}"')
            if ing.allowed_variants:
                av = ", ".join(f'"{v}"' for v in ing.allowed_variants)
                inner.append(f"allowedVariants: [ {av} ]")
            if ing.is_tool:
                inner.append("isTool: true")
                if ing.tool_cost and ing.tool_cost > 0:
                    inner.append(f"toolDurabilityCost: {int(ing.tool_cost)}")
            if ing.quantity and ing.quantity != 1:
                inner.append(f"quantity: {int(ing.quantity)}")
            lines.append(f'\t\t\t"{sym}": {{ ' + ", ".join(inner) + " }},")
        if lines[-1].endswith(","):
            lines[-1] = lines[-1][:-1]
        lines.append("\t\t},")
        lines.append(f"\t\twidth: {GRID_SIZE},")
        lines.append(f"\t\theight: {GRID_SIZE},")
        out_code = (self.output_code_var.get() or "").strip() or "game:__set_output__"
        out_inner = [f'type: "item"', f'code: "{out_code}"']
        try:
            q = max(1, int(self.output_qty_var.get()))
        except ValueError:
            q = 1
        if q != 1:
            out_inner.append(f"quantity: {q}")
        lines.append("\t\toutput: { " + ", ".join(out_inner) + " }")
        lines.append("\t},")
        lines.append("]")
        return "\n".join(lines)

    def copy_json5(self):
        text = self.build_json5_text()
        self.clipboard_clear()
        self.clipboard_append(text)
        messagebox.showinfo("Copied", "Recipe JSON5 copied to clipboard.\nPaste it into your file!")

# ---- Run ----
if __name__ == "__main__":
    app = RecipeBuilderApp()
    app.mainloop()
