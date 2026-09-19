// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AgentTaskStatus,
  AgentTaskStatusName,
  ModelCallFailed,
  TaskStatusRunning,
} from '../../core/api/models';
import { formatDuration, modelServed } from '../../shared/model-call-format';
import { StatusBadge } from '../../shared/status-badge';
import { TasksStore } from '../../state/tasks.store';

@Component({
  selector: 'bops-dashboard',
  imports: [DecimalPipe, FormsModule, StatusBadge],
  templateUrl: './dashboard.html',
})
export class Dashboard {
  protected readonly tasks = inject(TasksStore);
  protected readonly goal = signal('');

  protected readonly formatDuration = formatDuration;
  protected readonly modelServed = modelServed;
  protected readonly modelCallFailed = ModelCallFailed;

  /** Steps whose model-call details are open. Closed by default: the panel is for the curious, not the default view. */
  private readonly openModelInfo = signal<ReadonlySet<string>>(new Set());

  protected modelInfoKey(taskId: string, stepIndex: number): string {
    return `${taskId}:${stepIndex}`;
  }

  protected isModelInfoOpen(key: string): boolean {
    return this.openModelInfo().has(key);
  }

  protected toggleModelInfo(key: string): void {
    this.openModelInfo.update((open) => {
      const next = new Set(open);
      if (!next.delete(key)) {
        next.add(key);
      }

      return next;
    });
  }

  protected readonly statusOptions = (
    Object.entries(AgentTaskStatusName) as [string, string][]
  ).map(([value, label]) => ({ value: Number(value) as AgentTaskStatus, label }));

  /** How many finished tasks are rendered at once; the API has no server-side page size. */
  protected readonly historyLimit = 50;

  protected readonly isLive = computed(() => this.tasks.statusFilter() === TaskStatusRunning);

  /** Live view: everything running. History: newest first, capped at `historyLimit`. */
  protected readonly visibleTasks = computed(() => {
    const all = this.tasks.tasks();
    if (this.isLive()) {
      return all;
    }

    return [...all]
      .sort((a, b) => Date.parse(b.createdAtUtc) - Date.parse(a.createdAtUtc))
      .slice(0, this.historyLimit);
  });

  protected onFilterChange(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value) as AgentTaskStatus;
    void this.tasks.setStatusFilter(value);
  }
  protected async onStart(): Promise<void> {
    const goal = this.goal().trim();
    if (!goal) {
      return;
    }

    await this.tasks.start(goal);
    this.goal.set('');
  }

  protected select(taskId: string): void {
    this.tasks.selectTask(taskId);
  }

  protected async resume(taskId: string, event: Event): Promise<void> {
    event.stopPropagation();
    await this.tasks.resume(taskId);
  }

  protected shortGoal(goal: string): string {
    return goal.length > 60 ? `${goal.slice(0, 60)}…` : goal;
  }

  protected formatTime(iso: string): string {
    return new Date(iso).toLocaleTimeString();
  }
}
