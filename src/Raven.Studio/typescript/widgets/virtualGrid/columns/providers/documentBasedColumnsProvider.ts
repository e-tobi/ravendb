﻿/// <reference path="../../../../../typings/tsd.d.ts"/>

import virtualColumn = require("widgets/virtualGrid/columns/virtualColumn");
import checkedColumn = require("widgets/virtualGrid/columns/checkedColumn");
import actionColumn = require("widgets/virtualGrid/columns/actionColumn");
import hyperlinkColumn = require("widgets/virtualGrid/columns/hyperlinkColumn");
import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import appUrl = require("common/appUrl");
import collectionsTracker = require("common/helpers/database/collectionsTracker");
import database = require("models/resources/database");
import document = require("models/database/documents/document");
import virtualGridUtils = require("widgets/virtualGrid/virtualGridUtils");
import app = require("durandal/app");
import showDataDialog = require("viewmodels/common/showDataDialog");

type columnOptionsDto = {
    extraClass?: (item: document) => string;
}

type documentBasedColumnsProviderOpts = {
    showRowSelectionCheckbox?: boolean;
    enableInlinePreview?: boolean;
    customInlinePreview?: (doc: document) => void;
    showSelectAllCheckbox?: boolean;
    createHyperlinks?: boolean;
    columnOptions?: columnOptionsDto;
}

class documentBasedColumnsProvider {

    private static readonly minColumnWidth = 150;

    showRowSelectionCheckbox: boolean;
    private readonly db: database;
    private readonly gridController: virtualGridController<document>;
    private readonly enableInlinePreview: boolean;
    private readonly createHyperlinks: boolean;
    private readonly showSelectAllCheckbox: boolean;
    private readonly columnOptions: columnOptionsDto;
    private readonly customInlinePreview: (doc: document) => void;
    private readonly collectionTracker: collectionsTracker;

    private static readonly externalIdRegex = /^\w+\/\w+/ig;

    constructor(db: database, gridController: virtualGridController<document>, opts: documentBasedColumnsProviderOpts) {
        this.showRowSelectionCheckbox = _.isBoolean(opts.showRowSelectionCheckbox) ? opts.showRowSelectionCheckbox : false;
        this.db = db;
        this.gridController = gridController;
        this.enableInlinePreview = _.isBoolean(opts.enableInlinePreview) ? opts.enableInlinePreview : false;
        this.showSelectAllCheckbox = _.isBoolean(opts.showSelectAllCheckbox) ? opts.showSelectAllCheckbox : false;
        this.createHyperlinks = _.isBoolean(opts.createHyperlinks) ? opts.createHyperlinks : true;
        this.columnOptions = opts.columnOptions;
        this.customInlinePreview = opts.customInlinePreview || documentBasedColumnsProvider.showPreview;
        this.collectionTracker = collectionsTracker.default;
    }

    findColumns(viewportWidth: number, results: pagedResult<document>, prioritizedColumns?: string[]): virtualColumn[] {
        const columnNames = this.findColumnNames(results, Math.floor(viewportWidth / documentBasedColumnsProvider.minColumnWidth), prioritizedColumns);

        // Insert the row selection checkbox column as necessary.
        const initialColumns: virtualColumn[] = [];

        if (this.showRowSelectionCheckbox) {
            initialColumns.push(new checkedColumn(this.showSelectAllCheckbox));
        }

        if (this.enableInlinePreview) {
            const previewColumn = new actionColumn<document>(this.gridController, this.customInlinePreview, "Preview", `<i class="icon-preview"></i>`, "70px",
            {
                title: () => 'Show item preview'
            });
            initialColumns.push(previewColumn);
        }

        const initialColumnsWidth = _.sumBy(initialColumns, x => virtualGridUtils.widthToPixels(x));
        const rightScrollWidth = 5;
        const remainingSpaceForOtherColumns = viewportWidth - initialColumnsWidth - rightScrollWidth;
        const columnWidth = Math.floor(remainingSpaceForOtherColumns / columnNames.length) + "px";

        return initialColumns.concat(columnNames.map(p => {
            if (this.createHyperlinks) {
                if (p === "__metadata") {
                    return new hyperlinkColumn(this.gridController, (x: document) => x.getId(), x => appUrl.forEditDoc(x.getId(), this.db, x.__metadata.collection), "Id", columnWidth, this.columnOptions);
                }

                return new hyperlinkColumn(this.gridController, p, _.partial(this.findLink, _, p).bind(this), p, columnWidth, this.columnOptions);
            } else {
                if (p === "__metadata") {
                    return new textColumn(this.gridController, (x: document) => x.getId(), "Id", columnWidth, this.columnOptions);
                }
                return new textColumn(this.gridController, p, p, columnWidth, this.columnOptions);
            }
        }));
    }

    static showPreview(doc: document) {
        const docDto = doc.toDto(true);
        
        const text = JSON.stringify(docDto, null, 4);
        const title = doc.getId() ? "Document: " + doc.getId() : "Document preview";
        app.showBootstrapDialog(new showDataDialog(title, text, "javascript"));
    }

    static extractUniquePropertyNames(results: pagedResult<document>) {
        const uniquePropertyNames = new Set<string>();

        results.items
            .map(i => _.keys(i).forEach(key => uniquePropertyNames.add(key)));

        if (!_.every(results.items, (x: document) => x.__metadata && x.getId())) {
            uniquePropertyNames.delete("__metadata");
        }

        return Array.from(uniquePropertyNames);
    }

    private findColumnNames(results: pagedResult<document>, limit: number, prioritizedColumns?: string[]): string[] {
        const columnNames = documentBasedColumnsProvider.extractUniquePropertyNames(results);

        prioritizedColumns = prioritizedColumns || ["__metadata", "Name"];

        prioritizedColumns
            .reverse()
            .forEach(c => {
                const columnIndex = columnNames.indexOf(c);
                if (columnIndex >= 0) {
                    columnNames.splice(columnIndex, 1);
                    columnNames.unshift(c);
                }
            });

        if (columnNames.length > limit) {
            columnNames.length = limit;
        }

        return columnNames;
    }

    private findLink(item: document, property: string): string {
        if (property in item) {
            const value = (item as any)[property];

            //TODO: support url's in data as well
            if (_.isString(value) && value.match(documentBasedColumnsProvider.externalIdRegex)) {
                const extractedCollectionName = value.split("/")[0].toLowerCase();
                const matchedCollection = this.collectionTracker.getCollectionNames().find(collection => extractedCollectionName.startsWith(collection.toLowerCase()));
                return matchedCollection ? appUrl.forEditDoc(value, this.db, matchedCollection) : null;
            }
        }
        return null;
    }
}

export = documentBasedColumnsProvider;
