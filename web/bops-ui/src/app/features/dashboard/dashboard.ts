// Copyright 2026 Fabio Cavallari
// SPDX-License-Identifier: Apache-2.0

import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentTaskStatus, AgentTaskStatusName, TaskStatusRunning } from '../../core/api/models';
import { StatusBadge } from '../../shared/status-badge';
import { TasksStore } from '../../state/tasks.store';

@Component({
  selector: 'bops-dashboard',
  imports: [FormsModule, StatusBadge],
  templateUrl: './dashboard.html',
})
export class Dashboard {
  protected readonly tasks = inject(TasksStore);
  protected readonly goal = signal('');

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
