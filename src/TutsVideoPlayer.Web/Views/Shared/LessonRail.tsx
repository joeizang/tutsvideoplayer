import { useState } from "react";
import type { CourseTreeModel, CourseTreeNodeModel } from "dotnet:types/TutsVideoPlayer/Web/Models";
import { FolderClosedIcon, FolderOpenIcon } from "./Icons.tsx";

// The rail opens on the section being watched and nothing else: a long course is a wall of
// modules otherwise. Ancestors of the current lesson are walked up front so a nested module
// still opens along its whole path, and the same set is computed on the server render.
function ancestorFolderIds(nodes: readonly CourseTreeNodeModel[], lessonId: string) {
    const parentOf = new Map<string, string | null>();
    for (const node of nodes) {
        parentOf.set(node.id, node.folderId ?? null);
    }

    const open = new Set<string>();
    let current = parentOf.get(lessonId) ?? null;
    while (current !== null && !open.has(current)) {
        open.add(current);
        current = parentOf.get(current) ?? null;
    }

    return open;
}

export function LessonRail({ rail, currentLessonId }: { rail: CourseTreeModel; currentLessonId: string }) {
    const [expanded, setExpanded] = useState<Set<string>>(() => ancestorFolderIds(rail.nodes, currentLessonId));

    const childrenByFolder = new Map<string | null, CourseTreeNodeModel[]>();
    for (const node of rail.nodes) {
        const key = node.folderId ?? null;
        const siblings = childrenByFolder.get(key) ?? [];
        siblings.push(node);
        childrenByFolder.set(key, siblings);
    }

    const toggle = (folderId: string) => {
        setExpanded((current) => {
            const next = new Set(current);
            if (next.has(folderId)) {
                next.delete(folderId);
            } else {
                next.add(folderId);
            }
            return next;
        });
    };

    const renderNodes = (folderId: string | null, depth: number) => {
        const nodes = childrenByFolder.get(folderId) ?? [];
        return (
            <ul className={`m-0 list-none p-0 ${depth > 0 ? "ml-3 border-l border-ink/10 pl-2 dark:border-neutral-700" : ""}`} role={depth === 0 ? "tree" : "group"}>
                {nodes.map((node) => {
                    if (node.type === "folder") {
                        const isOpen = expanded.has(node.id);
                        return (
                            <li key={node.id} role="none" className="mt-1">
                                <button
                                    type="button"
                                    className="flex w-full items-center gap-2 rounded border-0 bg-transparent px-1.5 py-2 text-left text-sm font-semibold text-ink hover:bg-action/10 dark:text-neutral-100"
                                    aria-expanded={isOpen}
                                    onClick={() => toggle(node.id)}
                                >
                                    <span className="text-folder">
                                        {isOpen
                                            ? <FolderOpenIcon className="h-5 w-5 shrink-0" />
                                            : <FolderClosedIcon className="h-5 w-5 shrink-0" />}
                                    </span>
                                    <span className="min-w-0">{node.title}</span>
                                </button>
                                {isOpen ? renderNodes(node.id, depth + 1) : null}
                            </li>
                        );
                    }

                    const isCurrent = node.id === currentLessonId;
                    return (
                        <li key={node.id} role="none" className="leading-snug">
                            <a
                                href={`/watch/${node.id}`}
                                role="treeitem"
                                aria-current={isCurrent ? "true" : undefined}
                                className={`my-0.5 block rounded px-1.5 py-1 text-sm ${
                                    isCurrent
                                        ? "bg-action text-white"
                                        : "text-ink hover:bg-action/10 dark:text-neutral-200"
                                }`}
                            >
                                {!node.available ? (
                                    <span className="text-danger dark:text-red-400">missing · </span>
                                ) : null}
                                {node.completed && node.available ? (
                                    <span aria-label="completed" className="text-completion dark:text-emerald-400">✓ </span>
                                ) : null}
                                <span className={node.completed && node.available ? "text-ink-soft dark:text-neutral-400" : undefined}>
                                    {node.title}
                                </span>
                            </a>
                        </li>
                    );
                })}
            </ul>
        );
    };

    return (
        <nav className="w-full shrink-0 overflow-y-auto border-b border-ink/10 bg-surface p-4 lg:h-auto lg:w-[300px] lg:border-b-0 lg:border-r dark:border-neutral-700 dark:bg-neutral-800" aria-label="Course lessons">
            <h2 className="m-0 mb-3 text-base font-semibold">{rail.courseTitle}</h2>
            {!rail.available ? <p className="text-xs text-danger dark:text-red-400">This course is currently unavailable.</p> : null}
            {renderNodes(null, 0)}
        </nav>
    );
}
