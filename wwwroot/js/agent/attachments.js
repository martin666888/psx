AgentThreadManager.prototype._wireAttachments = function() {
        this.lastSubmittedDraft = null;
        if (!this.meta.attachButton || !this.meta.attachmentInput || !this.meta.attachmentStrip) {
            return;
        }

        this.meta.attachButton.addEventListener('click', () => {
            if (!this.supportsImage || this.isBusy || this.isRestoring) return;
            this.meta.attachmentInput.click();
        });

        this.meta.attachmentInput.addEventListener('change', () => {
            this._handleAttachmentFiles(Array.from(this.meta.attachmentInput.files || []));
            this.meta.attachmentInput.value = '';
        });

        this.panel.addEventListener('paste', (event) => {
            if (!this.supportsImage || this.isBusy || this.isRestoring) return;
            const files = Array.from(event.clipboardData?.items || [])
                .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
                .map((item) => item.getAsFile())
                .filter(Boolean);
            if (files.length > 0) {
                event.preventDefault();
                this._handleAttachmentFiles(files);
            }
        });

        this.panel.addEventListener('dragover', (event) => {
            if (!this.supportsImage || this.isBusy || this.isRestoring) return;
            if (Array.from(event.dataTransfer?.items || []).some((item) => item.kind === 'file')) {
                event.preventDefault();
            }
        });

        this.panel.addEventListener('drop', (event) => {
            if (!this.supportsImage || this.isBusy || this.isRestoring) return;
            const files = Array.from(event.dataTransfer?.files || []).filter((file) => file.type.startsWith('image/'));
            if (files.length > 0) {
                event.preventDefault();
                this._handleAttachmentFiles(files);
            }
        });

        if (this.meta.imagePreview) {
            this.meta.imagePreview.addEventListener('click', () => this._hideImagePreview());
        }

        document.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') {
                this._hideImagePreview();
            }
        });

        this._syncAttachmentControls();
};

AgentThreadManager.prototype._handleAttachmentFiles = function(files) {
        const images = files.filter((file) => file && file.type && file.type.startsWith('image/'));
        if (!this.supportsImage) {
            this._appendSystem('Current ACP Agent does not support image input.');
            return;
        }

        if (images.length === 0) return;

        const allowedTypes = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
        const maxSingle = 20 * 1024 * 1024;
        const maxTotal = 50 * 1024 * 1024;
        const maxCount = 5;
        let currentTotal = this.pendingAttachments.reduce((sum, item) => sum + (item.size || 0), 0);

        for (const file of images) {
            if (!allowedTypes.has(file.type)) {
                this._appendSystem('Only PNG, JPEG, WebP, and GIF images are supported.');
                continue;
            }

            if (file.size > maxSingle) {
                this._appendSystem(file.name + ' is larger than 20MB.');
                continue;
            }

            if (this.pendingAttachments.length >= maxCount) {
                this._appendSystem('You can attach at most 5 images at once.');
                break;
            }

            if (currentTotal + file.size > maxTotal) {
                this._appendSystem('Images in one message must total 50MB or less.');
                break;
            }

            currentTotal += file.size;
            this._uploadAttachmentFile(file);
        }
};

AgentThreadManager.prototype._uploadAttachmentFile = function(file) {
        const clientId = 'att-' + Date.now() + '-' + Math.random().toString(16).slice(2);
        const item = {
            clientId,
            id: '',
            fileName: file.name || 'image',
            mimeType: file.type,
            size: file.size,
            status: 'uploading',
            url: URL.createObjectURL(file),
            localPreviewUrl: true
        };
        this.pendingAttachments.push(item);
        this._renderPendingAttachments();

        const reader = new FileReader();
        reader.onload = () => {
            const dataUrl = String(reader.result || '');
            const comma = dataUrl.indexOf(',');
            Bridge.uploadAgentAttachment({
                clientId,
                fileName: item.fileName,
                mimeType: item.mimeType,
                size: item.size,
                dataBase64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl
            });
        };
        reader.onerror = () => {
            this._markAttachmentFailed(clientId, 'Failed to read image file.');
        };
        reader.readAsDataURL(file);
};

AgentThreadManager.prototype._handleAttachmentUploaded = function(event) {
        const item = this.pendingAttachments.find((attachment) => attachment.clientId === event.clientId);
        if (!item || !event.attachment) return;

        if (item.localPreviewUrl && item.url) {
            URL.revokeObjectURL(item.url);
        }

        Object.assign(item, event.attachment, {
            clientId: event.clientId,
            status: 'uploaded',
            localPreviewUrl: false
        });
        this._renderPendingAttachments();
};

AgentThreadManager.prototype._handleAttachmentFailed = function(event) {
        this._markAttachmentFailed(event.clientId, event.text || 'Image upload failed.');
};

AgentThreadManager.prototype._markAttachmentFailed = function(clientId, message) {
        const item = this.pendingAttachments.find((attachment) => attachment.clientId === clientId);
        if (!item) {
            this._appendSystem(message);
            return;
        }

        item.status = 'failed';
        item.error = message;
        this._appendSystem(message);
        this._renderPendingAttachments();
};

AgentThreadManager.prototype._renderPendingAttachments = function() {
        const strip = this.meta.attachmentStrip;
        if (!strip) return;

        strip.innerHTML = '';
        strip.hidden = this.pendingAttachments.length === 0;
        for (const attachment of this.pendingAttachments) {
            const tile = this._createAttachmentTile(attachment, true);
            strip.appendChild(tile);
        }
};

AgentThreadManager.prototype._createAttachmentTile = function(attachment, removable) {
        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'agent-attachment-tile agent-attachment-' + (attachment.status || 'ready');
        tile.title = attachment.fileName || 'Image attachment';

        const image = document.createElement('img');
        image.src = attachment.url;
        image.alt = attachment.fileName || 'Image attachment';
        tile.appendChild(image);

        if (attachment.status === 'uploading') {
            const status = document.createElement('span');
            status.className = 'agent-attachment-status';
            status.textContent = '...';
            tile.appendChild(status);
        }

        tile.addEventListener('click', () => this._showImagePreview(attachment.url));

        if (removable) {
            const remove = document.createElement('span');
            remove.className = 'agent-attachment-remove';
            remove.textContent = '×';
            remove.addEventListener('click', (event) => {
                event.stopPropagation();
                this._removePendingAttachment(attachment.clientId);
            });
            tile.appendChild(remove);
        }

        return tile;
};

AgentThreadManager.prototype._removePendingAttachment = function(clientId) {
        const index = this.pendingAttachments.findIndex((attachment) => attachment.clientId === clientId);
        if (index < 0) return;

        const [item] = this.pendingAttachments.splice(index, 1);
        if (item.localPreviewUrl && item.url) {
            URL.revokeObjectURL(item.url);
        }
        this._renderPendingAttachments();
};

AgentThreadManager.prototype._pendingAttachmentIds = function() {
        return this.pendingAttachments
            .filter((attachment) => attachment.status === 'uploaded' && attachment.id)
            .map((attachment) => attachment.id);
};

AgentThreadManager.prototype._attachmentsReady = function() {
        return !this.pendingAttachments.some((attachment) => attachment.status === 'uploading');
};

AgentThreadManager.prototype._clearPendingAttachments = function() {
        for (const item of this.pendingAttachments) {
            if (item.localPreviewUrl && item.url) {
                URL.revokeObjectURL(item.url);
            }
        }
        this.pendingAttachments = [];
        this._renderPendingAttachments();
};

AgentThreadManager.prototype._restoreSubmittedDraft = function() {
        if (!this.lastSubmittedDraft) return;
        this.input.value = this.lastSubmittedDraft.text || '';
        this.pendingAttachments = (this.lastSubmittedDraft.attachments || []).slice();
        this.lastSubmittedDraft = null;
        this._resizeInput();
        this._renderPendingAttachments();
};

AgentThreadManager.prototype._appendMessageAttachments = function(attachments) {
        if (!Array.isArray(attachments) || attachments.length === 0) return;
        const host = this.currentTurn || this.thread;
        const userBodies = host.querySelectorAll('.agent-message-user .agent-message-body');
        const body = userBodies[userBodies.length - 1];
        if (!body) return;

        const grid = document.createElement('div');
        grid.className = 'agent-message-attachments';
        attachments.forEach((attachment) => {
            grid.appendChild(this._createAttachmentTile(attachment, false));
        });

        const content = body.querySelector('.agent-message-content');
        if (content) {
            body.insertBefore(grid, content);
        } else {
            body.appendChild(grid);
        }
};

AgentThreadManager.prototype._showImagePreview = function(url) {
        if (!this.meta.imagePreview || !this.meta.imagePreviewImg || !url) return;
        this.meta.imagePreviewImg.src = url;
        this.meta.imagePreview.hidden = false;
};

AgentThreadManager.prototype._hideImagePreview = function() {
        if (!this.meta.imagePreview || !this.meta.imagePreviewImg) return;
        this.meta.imagePreview.hidden = true;
        this.meta.imagePreviewImg.removeAttribute('src');
};

AgentThreadManager.prototype._syncAttachmentControls = function() {
        if (!this.meta.attachButton) return;
        this.meta.attachButton.disabled = !this.supportsImage || this.isBusy || this.isRestoring;
        this.meta.attachButton.title = this.supportsImage
            ? 'Attach images'
            : 'Current ACP Agent does not support image input';
};
